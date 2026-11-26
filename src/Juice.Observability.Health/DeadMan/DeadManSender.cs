using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Juice.Notifications;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Health.Delivery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Health.DeadMan
{
    /// <summary>The dead-man alarm's causes (phase 5, M49).</summary>
    public static class DeadManCauses
    {
        /// <summary>Some stage has had work due and not taken for longer than <see cref="DeadManOptions.DueThreshold"/>.</summary>
        public const string Stalled = "stalled";

        /// <summary>Some stage kept <see cref="DeadManOptions.FailingThreshold"/> failing rows for <see cref="DeadManOptions.FailingFor"/>.</summary>
        public const string Failing = "failing";

        /// <summary>Failed attempts and none that got out, for <see cref="DeadManOptions.FailingFor"/>: nothing is getting out.</summary>
        public const string NoSuccess = "no-success";

        /// <summary>A delivery still in flight older than <see cref="DeadManOptions.UndeliveredThreshold"/>.</summary>
        public const string StuckInRetry = "stuck-in-retry";

        /// <summary>The shadow comparison is failing (<see cref="IShadowReport"/>, M47).</summary>
        public const string Shadow = "shadow";

        /// <summary>The pipeline's health couldn't be read (an error, or longer than <see cref="DeadManOptions.ReadTimeout"/>).</summary>
        public const string Unreadable = "unreadable";

        public static readonly string[] All = [Stalled, Failing, NoSuccess, StuckInRetry, Shadow, Unreadable];
    }

    /// <summary>
    /// The dead-man alarm (phase 5, M49; p5-d4): it reads the notification pipeline's health (<see cref="INotificationsPipelineHealth"/>) and,
    /// when the pipeline stops keeping up, posts to one endpoint of its own (<see cref="DeadManOptions"/>), signed and through the SSRF-checked
    /// client. It never sends through the pipeline it watches, and writes nothing to the pipeline's tables: if the pipeline can't be read, that is
    /// itself a cause.
    /// <para>One replica at a time (a lease). Its causes come from the database (due and failing rows across replicas), so they don't depend on
    /// which replica holds the lease; what it remembers (when a cause began, when it was sent) is in memory, so a failover may send once more.
    /// A cause is sent at most once per <see cref="DeadManOptions.ResendAfter"/>, and resolved when it clears.</para>
    /// </summary>
    public sealed class DeadManSender(IServiceScopeFactory scopes, IOptions<DeadManOptions> options, IOptions<PlatformOptions> platform, TimeProvider clock,
        ILogger<DeadManSender> logger, IShadowReport? shadow = null) : BackgroundService
    {
        public const string Worker = "notifications-deadman";

        private static readonly Meter Meter = new("Juice.Observability");
        private static readonly Counter<long> Sent = Meter.CreateCounter<long>("juice.observability.notifications.deadman", "{alarm}",
            "Dead-man alarms posted, by cause and event (firing, resolved) and outcome (sent, failed)");

        private sealed class State
        {
            public bool Active;
            public DateTimeOffset Since;
            public DateTimeOffset? SentAt;
            public string Detail = "";
        }

        private readonly Dictionary<string, State> _causes = DeadManCauses.All.ToDictionary(c => c, _ => new State());
        private readonly Dictionary<string, DateTimeOffset> _failingSince = [];
        private readonly Lazy<HttpClient> _client = new(() => SafeHttpClient.Create(options.Value.ConnectTimeout, options.Value.AllowHttpToAllowlisted,
            _ => ValueTask.FromResult<IReadOnlyList<AllowedDestination>>([.. options.Value.Allowlist.Select(a => new AllowedDestination(a.Destination, a.Ports))])));

        /// <summary>The causes standing now, and the pipeline as last read: for readiness.</summary>
        public DeadManStatus Status { get; private set; } = new([], null, null);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            PlatformCaller.Set(Worker, stoppingToken);
            WorkerPulse.Register(Worker, options.Value.Interval, stopping: stoppingToken);
            await using var lease = WorkerLease.For(scopes, Worker, logger);
            using var timer = new PeriodicTimer(options.Value.Interval);
            var held = false;
            do
            {
                try
                {
                    var holds = await lease.TryHoldAsync(stoppingToken);
                    if (holds && !held)
                    {
                        // This replica just took the lease (a start, or a failover): it doesn't know what the one before it told.
                        LeaseAcquired();
                    }
                    held = holds;
                    if (holds)
                    {
                        using var pass = WorkerPulse.Begin(Worker, stoppingToken);
                        await RunOnceAsync(stoppingToken);
                        pass.Succeeded();
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Dead-man pass failed; retrying next interval");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        /// <summary>Causes still to be reconciled with the receiver, since this replica took the lease.</summary>
        private readonly HashSet<string> _reconcile = [];

        /// <summary>
        /// The lease is this replica's now: on its next pass every cause is told as it stands, <c>firing</c> or <c>resolved</c>, marked
        /// <c>reconcile</c> (receivers take a resolve of a cause that isn't open as a no-op). So a cause that cleared while no replica was
        /// watching is resolved, and one standing is told again once. A cause whose state isn't known yet (the pipeline unread, or a condition
        /// that needs a second read) waits until it is.
        /// </summary>
        public void LeaseAcquired() => _reconcile.UnionWith(DeadManCauses.All);

        /// <summary>Reads the pipeline, decides each cause, and posts what is due: a cause arising (or still standing after the resend time), or one cleared.</summary>
        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            var now = clock.GetUtcNow();
            var (health, unreadable) = await ReadAsync(cancellationToken);
            var standing = new Dictionary<string, string?>
            {
                [DeadManCauses.Unreadable] = unreadable,
                // Unread, the pipeline's own causes are unknown: left as they were, neither raised nor resolved.
                [DeadManCauses.Stalled] = health is null ? Keep(DeadManCauses.Stalled) : Stalled(health),
                [DeadManCauses.Failing] = health is null ? Keep(DeadManCauses.Failing) : Failing(health, now),
                [DeadManCauses.NoSuccess] = health is null ? Keep(DeadManCauses.NoSuccess) : NoSuccess(health, now),
                [DeadManCauses.StuckInRetry] = health is null ? Keep(DeadManCauses.StuckInRetry) : StuckInRetry(health),
                [DeadManCauses.Shadow] = shadow is null ? null : await shadow.FailingAsync(cancellationToken),
            };
            // Not known yet: unread, or a condition seen once that needs a second read to stand.
            var unknown = new HashSet<string>();
            if (health is null)
            {
                unknown.UnionWith([DeadManCauses.Stalled, DeadManCauses.Failing, DeadManCauses.NoSuccess, DeadManCauses.StuckInRetry]);
            }
            if (standing[DeadManCauses.Failing] is null && _failingSince.Count > 0) unknown.Add(DeadManCauses.Failing);
            if (standing[DeadManCauses.NoSuccess] is null && _noSuccessSince is not null) unknown.Add(DeadManCauses.NoSuccess);
            foreach (var (cause, detail) in standing)
            {
                var state = _causes[cause];
                if (_reconcile.Contains(cause) && !unknown.Contains(cause))
                {
                    if (detail is not null && !state.Active)
                    {
                        (state.Active, state.Since) = (true, now);
                    }
                    state.Detail = detail ?? "";
                    if (await PostAsync(cause, detail is not null ? "firing" : "resolved", state, health, now, cancellationToken, reconcile: true))
                    {
                        _reconcile.Remove(cause);
                        // The rate limit runs from this post.
                        (state.Active, state.SentAt) = detail is not null ? (true, (DateTimeOffset?)now) : (false, null);
                    }
                    continue;
                }
                if (detail is not null)
                {
                    if (!state.Active)
                    {
                        (state.Active, state.Since, state.SentAt) = (true, now, null);
                    }
                    state.Detail = detail;
                    if (state.SentAt is not { } sent || now - sent >= options.Value.ResendAfter)
                    {
                        if (await PostAsync(cause, "firing", state, health, now, cancellationToken))
                        {
                            state.SentAt = now;
                        }
                    }
                }
                else if (state.Active)
                {
                    // Never told, nothing to take back; told, the resolve is sent (and tried again next pass if it fails).
                    if (state.SentAt is null || await PostAsync(cause, "resolved", state, health, now, cancellationToken))
                    {
                        (state.Active, state.SentAt, state.Detail) = (false, null, "");
                    }
                }
            }
            Status = new DeadManStatus([.. _causes.Where(c => c.Value.Active).Select(c => $"{c.Key}: {c.Value.Detail}")], health, now);
        }

        private string? Keep(string cause) => _causes[cause].Active ? _causes[cause].Detail : null;

        private async Task<(PipelineHealth? Health, string? Unreadable)> ReadAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Value.ReadTimeout);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var read = scope.ServiceProvider.GetRequiredService<INotificationsPipelineHealth>().ReadAsync(timeout.Token).AsTask();
                // A read that ignores its token still counts as too slow.
                var done = await Task.WhenAny(read, Task.Delay(options.Value.ReadTimeout, cancellationToken));
                return done == read ? (await read, null) : (null, $"no answer within {options.Value.ReadTimeout.TotalSeconds:0.#} s");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (null, $"no answer within {options.Value.ReadTimeout.TotalSeconds:0.#} s");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "The notification pipeline's health couldn't be read");
                return (null, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private string? Stalled(PipelineHealth health)
        {
            var stalled = health.Stages.Where(s => s.Value.OldestDue > options.Value.DueThreshold).Select(s => $"{s.Key} due {Minutes(s.Value.OldestDue!.Value)}").ToList();
            return stalled.Count > 0 ? string.Join(", ", stalled) : null;
        }

        private string? Failing(PipelineHealth health, DateTimeOffset now)
        {
            var failing = new List<string>();
            foreach (var (stage, stageHealth) in health.Stages)
            {
                if (stageHealth.Failing < options.Value.FailingThreshold)
                {
                    _failingSince.Remove(stage);
                    continue;
                }
                var since = _failingSince.TryGetValue(stage, out var first) ? first : _failingSince[stage] = now;
                // Two reads at least FailingFor apart: a burst that clears on retry isn't an alarm.
                if (now - since >= options.Value.FailingFor)
                {
                    failing.Add($"{stage} {stageHealth.Failing}{(stageHealth.Failing >= StageHealth.MaxFailing ? "+" : "")} failing for {Minutes(now - since)}");
                }
            }
            foreach (var gone in _failingSince.Keys.Except(health.Stages.Keys).ToList())
            {
                _failingSince.Remove(gone);
            }
            return failing.Count > 0 ? string.Join(", ", failing) : null;
        }

        private DateTimeOffset? _noSuccessSince;

        private string? NoSuccess(PipelineHealth health, DateTimeOffset now)
        {
            if (health.Sends.Failed < options.Value.MinFailedAttempts || health.Sends.Succeeded > 0)
            {
                _noSuccessSince = null;
                return null;
            }
            _noSuccessSince ??= now;
            // Two reads at least FailingFor apart: one bad minute isn't an alarm.
            return now - _noSuccessSince >= options.Value.FailingFor
                ? $"{health.Sends.Failed}{(health.Sends.Failed >= SendHealth.MaxCount ? "+" : "")} failed attempts and none sent in the last {Minutes(health.Sends.Window)}, for {Minutes(now - _noSuccessSince.Value)}"
                : null;
        }

        private string? StuckInRetry(PipelineHealth health)
            => health.Sends.OldestUndelivered is { } oldest && oldest > options.Value.UndeliveredThreshold ? $"a delivery in flight for {Minutes(oldest)}" : null;

        private static string Minutes(TimeSpan span) => span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.#} min" : $"{span.TotalSeconds:0} s";

        /// <summary>Posts one alarm, signed (Standard Webhooks); true when the endpoint took it (2xx).</summary>
        private async Task<bool> PostAsync(string cause, string @event, State state, PipelineHealth? health, DateTimeOffset now, CancellationToken cancellationToken,
            bool reconcile = false)
        {
            var id = Guid.CreateVersion7().ToString();
            var body = JsonSerializer.Serialize(new
            {
                id,
                type = $"observability.deadman.{@event}",
                cause,
                detail = state.Detail,
                // Told again by a replica that just took the lease: a resolve of a cause that isn't open is a no-op.
                reconcile,
                since = state.Since,
                occurredAt = now,
                tenant = platform.Value.TenantKey,
                // For diagnosis only: pass and progress times are this replica's, and never a cause on their own.
                pipeline = health is null ? null : new
                {
                    running = health.Running,
                    oldestDue = health.OldestDueAny?.TotalSeconds,
                    sends = new { window = health.Sends.Window.TotalSeconds, succeeded = health.Sends.Succeeded, failed = health.Sends.Failed,
                        oldestUndelivered = health.Sends.OldestUndelivered?.TotalSeconds },
                    stages = health.Stages.ToDictionary(s => s.Key, s => new
                    {
                        oldestDue = s.Value.OldestDue?.TotalSeconds, failing = s.Value.Failing, lastPassEndedAt = s.Value.LastPassEndedAt,
                        lastProgressAt = s.Value.LastProgressAt,
                    }),
                },
            }, JsonSerializerOptions.Web);
            var tags = new[] { new KeyValuePair<string, object?>("cause", cause), new KeyValuePair<string, object?>("event", @event) };
            try
            {
                var timestamp = now.ToUnixTimeSeconds();
                using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                request.Headers.Add(WebhookSigner.IdHeader, id);
                request.Headers.Add(WebhookSigner.TimestampHeader, timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
                request.Headers.Add(WebhookSigner.SignatureHeader, WebhookSigner.Sign(SecretReference.Resolve(options.Value.SecretRef!), id, timestamp, body));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.Value.RequestTimeout);
                using var response = await _client.Value.SendAsync(request, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    Sent.Add(1, [.. tags, new KeyValuePair<string, object?>("outcome", "sent")]);
                    logger.Log(@event == "firing" ? LogLevel.Error : LogLevel.Information, "Dead-man {Event}: {Cause} ({Detail})", @event, cause, state.Detail);
                    return true;
                }
                logger.LogError("Dead-man {Event} {Cause}: the endpoint answered {Status}", @event, cause, (int)response.StatusCode);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Dead-man {Event} {Cause} couldn't be posted: {Message}", @event, cause, ex.Message);
            }
            Sent.Add(1, [.. tags, new KeyValuePair<string, object?>("outcome", "failed")]);
            return false;
        }

        public override void Dispose()
        {
            if (_client.IsValueCreated) _client.Value.Dispose();
            base.Dispose();
        }
    }

    /// <summary>The causes standing (with their detail), and the pipeline as last read, when.</summary>
    public sealed record DeadManStatus(IReadOnlyList<string> Causes, PipelineHealth? Pipeline, DateTimeOffset? ReadAt);

    /// <summary>
    /// Readiness (<c>notifications-deadman</c>): degraded while a cause stands, with the pipeline's pass and progress times for diagnosis. Degraded,
    /// not unhealthy: the platform is ready; its notifications are what the alarm is about.
    /// </summary>
    public sealed class DeadManHealthCheck(DeadManSender sender) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var status = sender.Status;
            var data = new Dictionary<string, object>();
            if (status.ReadAt is { } read) data["readAt"] = read;
            foreach (var (stage, health) in status.Pipeline?.Stages ?? new Dictionary<string, StageHealth>())
            {
                if (health.LastPassEndedAt is { } pass) data[$"{stage}.lastPassEndedAt"] = pass;
                if (health.LastProgressAt is { } progress) data[$"{stage}.lastProgressAt"] = progress;
            }
            return Task.FromResult(status.Causes.Count > 0
                ? HealthCheckResult.Degraded($"The notification pipeline's dead-man: {string.Join("; ", status.Causes)}", data: data)
                : HealthCheckResult.Healthy(data: data));
        }
    }
}
