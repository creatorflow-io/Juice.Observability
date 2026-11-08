using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.MediatR;
using Juice.Messaging;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Webhooks
{
    /// <summary>Configuration section <c>Webhooks</c>.</summary>
    public sealed class WebhookOptions
    {
        public const string Section = "Webhooks";

        public bool Enabled { get; set; } = true;
        public List<EndpointDefinition> Endpoints { get; set; } = [];
        /// <summary>Private destinations webhooks may reach (requirements §8: the deployment runs inside the customer network).</summary>
        public List<AllowlistDefinition> Allowlist { get; set; } = [];
        /// <summary>Plain HTTP to allowlisted private destinations. HTTPS is required everywhere else.</summary>
        public bool AllowHttpToAllowlisted { get; set; }
        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);
        public int BatchSize { get; set; } = 50;
        public int MaxParallel { get; set; } = 4;
        public int MaxAttempts { get; set; } = 8;
        public TimeSpan RetryBase { get; set; } = TimeSpan.FromSeconds(10);
        public TimeSpan RetryMax { get; set; } = TimeSpan.FromMinutes(10);
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>Response bytes read; the rest is discarded.</summary>
        public int MaxResponseBytes { get; set; } = 64 * 1024;
        /// <summary>A secret reference set through the API may name only environment variables with this prefix.</summary>
        public string ApiSecretEnvPrefix { get; set; } = "JUICE_WEBHOOK_";
        /// <summary>A secret reference set through the API may name only files directly in this folder; unset refuses files.</summary>
        public string? ApiSecretDirectory { get; set; }
        /// <summary>Hosts a Slack or Teams channel may post to beyond those services' own, such as a compatible server inside the network.</summary>
        public List<string> AdditionalChatHosts { get; set; } = [];
        /// <summary>Where the Console is, for links in notifications; unset leaves them out.</summary>
        public string? ConsoleUrl { get; set; }
        public SmtpOptions Smtp { get; set; } = new();
    }

    /// <summary>The server email channels send through. Part of the deployment, never set through the API.</summary>
    public sealed class SmtpOptions
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 587;
        public string From { get; set; } = "";
        /// <summary>Requires STARTTLS.</summary>
        public bool UseTls { get; set; } = true;
        public string? UsernameRef { get; set; }
        public string? PasswordRef { get; set; }
        public bool IsConfigured => Host.Length > 0 && From.Length > 0;
    }

    /// <summary>Where Slack and Teams channels may post. The address still goes through the same SSRF checks as any webhook.</summary>
    public static class ChatDestination
    {
        private static readonly string[] SlackHosts = ["hooks.slack.com"];
        private static readonly string[] TeamsSuffixes = [".webhook.office.com", ".logic.azure.com", ".api.powerplatform.com"];

        public static bool IsAllowed(ChannelKind kind, string url, IReadOnlyCollection<string> additional)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0)
            {
                return false;
            }
            var host = uri.Host;
            if (additional.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
            return uri.Scheme == "https" && kind switch
            {
                ChannelKind.Slack => SlackHosts.Contains(host, StringComparer.OrdinalIgnoreCase),
                ChannelKind.Teams => TeamsSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };
        }
    }

    public sealed partial class EndpointDefinition
    {
        public string Key { get; set; } = "";
        public ChannelKind Kind { get; set; }
        /// <summary>A signed webhook's address. Slack and Teams keep theirs behind <see cref="SecretRef"/>, since it's a credential.</summary>
        public string Url { get; set; } = "";
        public string SecretRef { get; set; } = "";
        public List<string> Recipients { get; set; } = [];
        public List<string> Environments { get; set; } = [];
        public int GroupWindowSeconds { get; set; }
        public List<string> Events { get; set; } = [NotificationPlan.Firing, NotificationPlan.Resolved];
        public List<string> RuleKeys { get; set; } = [];
        public AlertSeverity MinimumSeverity { get; set; } = AlertSeverity.Info;
        public bool Enabled { get; set; } = true;

        public IEnumerable<string> Validate(bool allowHttp)
        {
            var at = $"webhook '{Key}'";
            if (!KeyPattern().IsMatch(Key)) yield return $"{at}: Key must match {KeyPattern()}";
            if (!Enum.IsDefined(Kind)) yield return $"{at}: unknown Kind";
            if (Kind == ChannelKind.Webhook)
            {
                if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0)
                    yield return $"{at}: Url must be an absolute https URL without credentials";
                else if (uri.Scheme == "http" && !allowHttp)
                    yield return $"{at}: Url must be https (AllowHttpToAllowlisted is off)";
            }
            else if (Url.Length > 0)
            {
                yield return $"{at}: only a signed webhook takes Url; a {Kind} channel's address is behind SecretRef";
            }
            if (Kind == ChannelKind.Email)
            {
                if (SecretRef.Length > 0) yield return $"{at}: an email channel takes no SecretRef";
                if (Recipients.Count is 0 or > MaxRecipients) yield return $"{at}: an email channel takes 1 to {MaxRecipients} Recipients";
                foreach (var recipient in Recipients.Where(r => !MailAddress.TryCreate(r, out _))) yield return $"{at}: '{recipient}' isn't an email address";
            }
            else
            {
                if (!SecretReference.IsValid(SecretRef)) yield return $"{at}: SecretRef must be env:NAME or file:/path";
                if (Recipients.Count > 0) yield return $"{at}: only an email channel takes Recipients";
            }
            if (GroupWindowSeconds is < 0 or > 3600) yield return $"{at}: GroupWindowSeconds must be 0 to 3600";
            if (Environments.Any(e => e.Length is 0 or > 256 || e != e.ToLowerInvariant())) yield return $"{at}: Environments are lowercase keys";
            if (Events.Count == 0 || Events.Any(e => e is not (NotificationPlan.Firing or NotificationPlan.Resolved)))
                yield return $"{at}: Events are {NotificationPlan.Firing} and/or {NotificationPlan.Resolved}";
        }

        public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));

        public const int MaxRecipients = 20;

        [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
        private static partial Regex KeyPattern();
    }

    public sealed class AllowlistDefinition
    {
        public string Destination { get; set; } = "";
        public List<int> Ports { get; set; } = [];
    }

    /// <summary>Plan the notifications of one alert transition: a channel's deliveries and, on firing, the escalations that match.</summary>
    public sealed record PlanNotifications(Guid PlatformTenantId, Guid TransitionId, DateTimeOffset CommittedAt) : MessageBase, IRequest<int>;

    /// <summary>
    /// Hears that an alert transition was committed, from the outbox (the <c>local</c> route: right after the commit, and from delivery if that failed), and
    /// plans its notifications as a command of its own.
    /// </summary>
    public sealed class AlertTransitionedConsumer(IMediator mediator) : IIntegrationEventHandler<AlertTransitionedIntegrationEvent>
    {
        // In UTC: the message serializer reads a time back in the host's offset, and PostgreSQL stores only UTC offsets.
        public async Task HandleAsync(AlertTransitionedIntegrationEvent @event)
            => await mediator.Send(new PlanNotifications(@event.PlatformTenantId, @event.TransitionId, @event.CreatedAt.ToUniversalTime()));
    }

    /// <summary>
    /// Plans a transition's notifications once. The outbox may hand a transition over twice, so a transition planned already is left alone, and two plans
    /// racing on different replicas meet at <see cref="AlertTransition.NotificationsPlannedAt"/>, a concurrency token: the second rolls back and, retried, finds it planned.
    /// </summary>
    public sealed class PlanNotificationsHandler(PlatformDbContext db, IOptions<PlatformOptions> platform, TimeProvider clock) : IRequestHandler<PlanNotifications, int>
    {
        public async ValueTask<int> Handle(PlanNotifications request, CancellationToken cancellationToken)
        {
            var transition = await db.AlertTransitions.SingleOrDefaultAsync(t => t.TenantId == request.PlatformTenantId && t.Id == request.TransitionId, cancellationToken);
            if (transition is null || transition.NotificationsPlannedAt is not null)
            {
                return 0;
            }
            var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.Id == transition.AlertId, cancellationToken);
            var rule = await db.Rules.AsNoTracking().SingleAsync(r => r.Id == alert.RuleId, cancellationToken);
            var now = clock.GetUtcNow();
            transition.NotificationsPlannedAt = now;
            // From when the transition was committed, so the dispatch delay still counts the time it spent in the outbox.
            return NotificationPlan.Plan(db, platform.Value.TenantKey, rule, alert, transition, request.CommittedAt, now);
        }
    }

    /// <summary>
    /// What a transition sends: one delivery per matching endpoint, held if a silence covers it, and on firing the escalations that match. The payload is
    /// fixed here, so every attempt sends and signs the same bytes. Only adds entities: the command's TransactionBehavior saves them.
    /// </summary>
    public static class NotificationPlan
    {
        public const string Firing = "alert.firing";
        public const string Resolved = "alert.resolved";
        /// <summary>Sent by an escalation policy, to the endpoints it names, whatever events they take otherwise.</summary>
        public const string Escalated = "alert.escalated";
        /// <summary>A test an admin sent to check a channel; never planned for an alert.</summary>
        public const string Test = "webhook.test";

        /// <returns>How many deliveries it added.</returns>
        public static int Plan(PlatformDbContext db, string tenantKey, Rule rule, Alert alert, AlertTransition transition, DateTimeOffset createdAt, DateTimeOffset now)
        {
            var type = transition.ToState == AlertState.Firing ? Firing : Resolved;
            var context = new AlertContext(rule.Key, alert.SubjectKey, alert.Severity);
            var silences = db.Silences.AsNoTracking().Where(s => s.TenantId == alert.TenantId && s.CancelledAt == null && s.StartsAt <= now && (s.RepeatWeekly || s.EndsAt > now)).ToList();
            var silence = SilenceMatcher.Find(silences, context, now);
            var endpoints = db.WebhookEndpoints.AsNoTracking().Where(e => e.TenantId == alert.TenantId && e.Enabled).ToList()
                .Where(e => e.Events.Contains(type) && (e.RuleKeys.Length == 0 || e.RuleKeys.Contains(rule.Key)) && alert.Severity >= e.MinimumSeverity
                    && (e.Environments.Length == 0 || context.Environment is { } environment && e.Environments.Contains(environment)));
            var added = 0;
            foreach (var endpoint in endpoints)
            {
                var id = Guid.CreateVersion7();
                db.WebhookDeliveries.Add(new WebhookDelivery
                {
                    Id = id, TenantId = alert.TenantId, EndpointId = endpoint.Id, AlertId = alert.Id, TransitionId = transition.Id,
                    Event = type, Payload = Payload(id, type, tenantKey, rule, alert, transition, null), CreatedAt = createdAt,
                    NextAttemptAt = now + TimeSpan.FromSeconds(endpoint.GroupWindowSeconds),
                    Status = silence is null ? DeliveryStatus.Pending : DeliveryStatus.Suppressed, SuppressedBy = silence?.Id,
                });
                added++;
            }
            // An alert that resolved before its firing was planned has nothing left to escalate.
            if (type == Firing && alert.State == AlertState.Firing)
            {
                StartEscalations(db, alert, context, now);
            }
            return added;
        }

        /// <summary>Every enabled policy matching the alert, from its first step. A policy with no way to reach anyone would never have been accepted.</summary>
        private static void StartEscalations(PlatformDbContext db, Alert alert, AlertContext context, DateTimeOffset now)
        {
            foreach (var policy in db.EscalationPolicies.AsNoTracking().Where(p => p.TenantId == alert.TenantId && p.Enabled).ToList())
            {
                if (policy.Steps.Count == 0 || alert.Severity < policy.MinimumSeverity || (policy.RuleKeys.Length > 0 && !policy.RuleKeys.Contains(context.RuleKey))
                    || (policy.Environments.Length > 0 && (context.Environment is not { } environment || !policy.Environments.Contains(environment))))
                {
                    continue;
                }
                db.AlertEscalations.Add(new AlertEscalation
                {
                    TenantId = alert.TenantId, AlertId = alert.Id, PolicyId = policy.Id, State = EscalationState.Active,
                    NextAt = (alert.FiredAt ?? now) + TimeSpan.FromMinutes(policy.Steps[0].AfterMinutes),
                });
            }
        }

        internal static string Payload(Guid id, string type, string tenant, Rule rule, Alert alert, AlertTransition transition, (string Policy, int Step)? escalation)
            => JsonSerializer.Serialize(new
            {
                id,
                type,
                occurredAt = transition.At,
                tenant,
                escalation = escalation is { } e ? new { policy = e.Policy, step = e.Step } : null,
                alert = new
                {
                    id = alert.Id,
                    rule = rule.Key,
                    ruleName = rule.Name,
                    severity = alert.Severity.ToString(),
                    state = transition.ToState.ToString(),
                    subject = alert.SubjectKey,
                    value = transition.Value,
                    breachStartedAt = alert.BreachStartedAt,
                    firedAt = alert.FiredAt,
                    resolvedAt = alert.ResolvedAt,
                    reason = transition.Reason,
                },
                links = new { alert = $"/api/v1/tenants/{alert.TenantId}/alerts/{alert.Id}" },
            }, JsonSerializerOptions.Web);
    }

    /// <summary>
    /// Sends due deliveries: signs, posts through the SSRF-checked client, records the attempt, and schedules retries
    /// with exponential backoff and jitter for transient failures (network, timeout, 408, 429, 5xx) up to
    /// <see cref="WebhookOptions.MaxAttempts"/>. Other responses and blocked destinations fail at once. Nothing here
    /// touches alert state. A silence that started after a delivery was queued still holds it, checked just before sending.
    /// </summary>
    public sealed class WebhookDispatcher(IServiceScopeFactory scopes, IOptions<WebhookOptions> options, TimeProvider clock,
        ILogger<WebhookDispatcher> logger) : BackgroundService
    {
        private static readonly Meter Meter = new("Juice.Observability");
        private static readonly Counter<long> Attempts = Meter.CreateCounter<long>("juice.observability.webhooks.attempts", "{attempt}",
            "Webhook send attempts, by outcome (delivered, retry, failed, blocked)");
        private static readonly Histogram<double> DispatchDelay = Meter.CreateHistogram<double>("juice.observability.webhooks.dispatch_delay", "s",
            "From the alert transition to the first attempt (requirements §9: p95 ≤ 30 s)");

        /// <summary>
        /// The allowlist is a tenant's, and a pooled connection skips the connect check, so tenants never share a client; each client is for the
        /// allowlist it was made under (its fingerprint), and is replaced when that changes (<see cref="ClientAsync"/>).
        /// </summary>
        private readonly ConcurrentDictionary<Guid, (string Fingerprint, Lazy<HttpClient> Client)> _clients = new();

        private const int MaxGroup = 100;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(options.Value.Interval);
            do
            {
                try
                {
                    while (await DispatchOnceAsync(stoppingToken) == options.Value.BatchSize) { }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Webhook dispatch failed; retrying next interval");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        /// <summary>Sends up to one batch of due deliveries; returns how many were attempted.</summary>
        public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
        {
            List<WebhookDelivery> due;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var now = clock.GetUtcNow();
                due = await db.WebhookDeliveries.AsNoTracking()
                    .Where(d => (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Retrying) && d.NextAttemptAt <= now)
                    .OrderBy(d => d.NextAttemptAt).Take(options.Value.BatchSize).ToListAsync(cancellationToken);
            }
            await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxParallel, CancellationToken = cancellationToken },
                async (delivery, ct) => await SendAsync(delivery, ct));
            return due.Count;
        }

        private async Task SendAsync(WebhookDelivery claimed, CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var now = clock.GetUtcNow();
            // Claim: move the due time forward only if nobody else did, so two dispatchers never send the same attempt.
            var lease = now + options.Value.RequestTimeout + options.Value.ConnectTimeout + TimeSpan.FromSeconds(5);
            if (await db.WebhookDeliveries.Where(d => d.Id == claimed.Id && d.NextAttemptAt == claimed.NextAttemptAt && d.Attempts == claimed.Attempts)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, lease), cancellationToken) == 0)
            {
                return;
            }
            var delivery = await db.WebhookDeliveries.SingleAsync(d => d.Id == claimed.Id, cancellationToken);
            var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == delivery.EndpointId, cancellationToken);

            var silences = await NotificationWorker.ActiveAsync(db, now, cancellationToken);
            if (await SilencingAsync(db, delivery, silences, now, cancellationToken) is { } silence)
            {
                delivery.Status = DeliveryStatus.Suppressed;
                delivery.SuppressedBy = silence.Id;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            if (endpoint.GroupWindowSeconds > 0 && delivery.Attempts == 0 && !await GatherGroupAsync(db, delivery, endpoint, silences, now, cancellationToken))
            {
                return;
            }

            var number = delivery.Attempts + 1;
            var started = Stopwatch.GetTimestamp();
            var (status, error, retry) = endpoint.Enabled
                ? await AttemptAsync(db, endpoint, delivery.Id, delivery.Payload, now, cancellationToken)
                : (null, "endpoint_disabled", false);

            delivery.Attempts = number;
            delivery.LastStatusCode = status;
            delivery.LastError = error;
            delivery.FirstAttemptAt ??= now;
            if (number == 1) DispatchDelay.Record((now - delivery.CreatedAt).TotalSeconds);
            string outcome;
            if (error is null)
            {
                delivery.Status = DeliveryStatus.Delivered;
                delivery.DeliveredAt = clock.GetUtcNow();
                outcome = "delivered";
            }
            else if (retry && number < options.Value.MaxAttempts)
            {
                delivery.Status = DeliveryStatus.Retrying;
                delivery.NextAttemptAt = clock.GetUtcNow() + Backoff(number);
                outcome = "retry";
            }
            else
            {
                delivery.Status = DeliveryStatus.Failed;
                outcome = error.StartsWith("blocked", StringComparison.Ordinal) || error.StartsWith("private", StringComparison.Ordinal)
                    || error == "https_required" ? "blocked" : "failed";
                logger.LogWarning("Webhook delivery {Delivery} to {Endpoint} failed after {Attempts} attempts: {Error}",
                    delivery.Id, endpoint.Key, number, error);
            }
            db.WebhookAttempts.Add(new WebhookAttempt
            {
                TenantId = delivery.TenantId, DeliveryId = delivery.Id, Number = number, At = now, StatusCode = status, Error = error,
                DurationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            });
            await db.SaveChangesAsync(cancellationToken);
            Attempts.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        }

        /// <summary>
        /// One send of a payload to an endpoint, the way every delivery goes: signed or written for its channel, through the SSRF-checked client.
        /// Returns the response status, the error if any, and whether the error is worth retrying.
        /// </summary>
        private async Task<(int? Status, string? Error, bool Retry)> AttemptAsync(PlatformDbContext db, WebhookEndpoint endpoint, Guid id, string payload, DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            if (endpoint.Kind == ChannelKind.Email)
            {
                var (mailError, mailRetry) = await SendEmailAsync(endpoint, payload, cancellationToken);
                return (null, mailError, mailRetry);
            }
            try
            {
                using var request = BuildRequest(endpoint, id, payload, now);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.Value.RequestTimeout);
                var client = await ClientAsync(db, endpoint.TenantId, cancellationToken);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                await DrainAsync(response, options.Value.MaxResponseBytes, timeout.Token);
                return (status, response.IsSuccessStatusCode ? null : $"status_{status}", status is 408 or 429 or >= 500);
            }
            catch (HttpRequestException ex) when (ex.InnerException is DestinationBlockedException blocked)
            {
                return (null, blocked.Reason, false);
            }
            catch (ChatDestinationRefusedException)
            {
                return (null, "chat_url_not_allowed", false);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Webhook secret", StringComparison.Ordinal))
            {
                return (null, "secret_unavailable", true);   // a secret mounted late should not lose the notification
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException && !cancellationToken.IsCancellationRequested)
            {
                return (null, ex is OperationCanceledException ? "timeout" : "network_error", true);
            }
        }

        /// <summary>
        /// The tenant's client, made again whenever its allowlist changed: the allowlist is checked when a connection opens, so a pooled connection
        /// to a destination just removed would otherwise keep working until it's recycled. The old client is disposed once its requests are done.
        /// </summary>
        private async Task<HttpClient> ClientAsync(PlatformDbContext db, Guid tenantId, CancellationToken cancellationToken)
        {
            var entries = await db.WebhookAllowlist.AsNoTracking().Where(a => a.TenantId == tenantId).OrderBy(a => a.Destination)
                .Select(a => new { a.Destination, a.Ports }).ToListAsync(cancellationToken);
            var fingerprint = string.Join(';', entries.Select(e => $"{e.Destination}:{string.Join(',', e.Ports)}"));
            while (true)
            {
                var current = _clients.GetOrAdd(tenantId, tenant => (fingerprint, new Lazy<HttpClient>(() => CreateClient(scopes, options.Value, tenant))));
                if (current.Fingerprint == fingerprint)
                {
                    return current.Client.Value;
                }
                var fresh = (fingerprint, new Lazy<HttpClient>(() => CreateClient(scopes, options.Value, tenantId)));
                if (_clients.TryUpdate(tenantId, fresh, current))
                {
                    if (current.Client.IsValueCreated)
                    {
                        var old = current.Client.Value;
                        _ = Task.Delay(options.Value.RequestTimeout + TimeSpan.FromSeconds(5), CancellationToken.None).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
                    }
                    return fresh.Item2.Value;
                }
            }
        }

        /// <summary>
        /// Sends a test notification to an endpoint now, through the same path as every delivery, and returns what happened. Nothing is stored
        /// here and nothing retries: the caller records it.
        /// </summary>
        public async Task<WebhookTestResult> TestAsync(WebhookEndpoint endpoint, string requestedBy, CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var now = clock.GetUtcNow();
            var id = Guid.CreateVersion7();
            var payload = TestPayload(id, endpoint, requestedBy, now);
            var started = Stopwatch.GetTimestamp();
            var (status, error, _) = await AttemptAsync(db, endpoint, id, payload, now, cancellationToken);
            Attempts.Add(1, new KeyValuePair<string, object?>("outcome", error is null ? "test_delivered" : "test_failed"));
            return new WebhookTestResult(id, error is null, status, error, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        /// <summary>A notification of type <see cref="NotificationPlan.Test"/>, shaped like an alert's so every channel can show it.</summary>
        internal static string TestPayload(Guid id, WebhookEndpoint endpoint, string requestedBy, DateTimeOffset now)
            => JsonSerializer.Serialize(new
            {
                id,
                type = NotificationPlan.Test,
                occurredAt = now,
                tenant = endpoint.TenantId,
                alert = new
                {
                    id = Guid.Empty,
                    rule = "test",
                    ruleName = $"Test notification for {endpoint.Key}",
                    severity = AlertSeverity.Info.ToString(),
                    state = "Test",
                    subject = endpoint.Key,
                    value = (double?)null,
                    reason = $"Sent by {requestedBy} to check this channel. No alert is behind it.",
                },
            }, JsonSerializerOptions.Web);

        private static async Task<Silence?> SilencingAsync(PlatformDbContext db, WebhookDelivery delivery, List<Silence> silences, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var mine = silences.Where(s => s.TenantId == delivery.TenantId).ToList();
            if (mine.Count == 0)
            {
                return null;
            }
            var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.Id == delivery.AlertId, cancellationToken);
            var rule = await db.Rules.AsNoTracking().Where(r => r.Id == alert.RuleId).Select(r => r.Key).SingleAsync(cancellationToken);
            return SilenceMatcher.Find(mine, new AlertContext(rule, alert.SubjectKey, alert.Severity), now);
        }

        /// <summary>
        /// A first attempt on a grouping endpoint takes every delivery waiting for the same endpoint and event, due or not, and carries them in its payload. Returns
        /// false when another dispatcher's group already took this one. The lock is per endpoint, so two can't each take the other.
        /// </summary>
        private static async Task<bool> GatherGroupAsync(PlatformDbContext db, WebhookDelivery delivery, WebhookEndpoint endpoint, List<Silence> silences, DateTimeOffset now, CancellationToken cancellationToken)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({WorkerLease.KeyOf($"delivery-group:{endpoint.Id}")})", cancellationToken);
            await db.Entry(delivery).ReloadAsync(cancellationToken);
            if (delivery.Status != DeliveryStatus.Pending)
            {
                return false;
            }
            var waiting = await db.WebhookDeliveries
                .Where(d => d.EndpointId == endpoint.Id && d.Event == delivery.Event && d.Id != delivery.Id && d.Status == DeliveryStatus.Pending && d.Attempts == 0)
                .OrderBy(d => d.Id).Take(MaxGroup - 1).ToListAsync(cancellationToken);
            var taken = new List<WebhookDelivery>();
            foreach (var other in waiting)
            {
                if (await SilencingAsync(db, other, silences, now, cancellationToken) is { } silence)
                {
                    other.Status = DeliveryStatus.Suppressed;
                    other.SuppressedBy = silence.Id;
                    continue;
                }
                other.Status = DeliveryStatus.Grouped;
                other.GroupedInto = delivery.Id;
                taken.Add(other);
            }
            if (taken.Count > 0)
            {
                delivery.Payload = NotificationView.Group(delivery.Id, delivery.Event, now, [delivery.Payload, .. taken.Select(d => d.Payload)]);
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        /// <summary>A signed webhook sends the payload itself; chat channels get it written for people, at an address only their secret knows.</summary>
        private HttpRequestMessage BuildRequest(WebhookEndpoint endpoint, Guid id, string payload, DateTimeOffset now)
        {
            if (endpoint.Kind == ChannelKind.Webhook)
            {
                var timestamp = now.ToUnixTimeSeconds();
                var signed = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
                signed.Headers.Add(WebhookSigner.IdHeader, id.ToString());
                signed.Headers.Add(WebhookSigner.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
                signed.Headers.Add(WebhookSigner.SignatureHeader, WebhookSigner.Sign(SecretReference.Resolve(endpoint.SecretRef), id.ToString(), timestamp, payload));
                return signed;
            }
            var url = SecretReference.Resolve(endpoint.SecretRef);
            if (!ChatDestination.IsAllowed(endpoint.Kind, url, options.Value.AdditionalChatHosts))
            {
                throw new ChatDestinationRefusedException();
            }
            var view = NotificationView.Parse(payload);
            var body = endpoint.Kind == ChannelKind.Slack ? ChannelFormatter.SlackBody(view, options.Value.ConsoleUrl) : ChannelFormatter.TeamsBody(view, options.Value.ConsoleUrl);
            return new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        /// <summary>The error, if any, and whether it's worth retrying: a server's 4xx and a lost connection are, its 5xx says the mail will never be accepted.</summary>
        private async Task<(string? Error, bool Retry)> SendEmailAsync(WebhookEndpoint endpoint, string payload, CancellationToken cancellationToken)
        {
            var smtp = options.Value.Smtp;
            if (!smtp.IsConfigured)
            {
                return ("smtp_not_configured", false);
            }
            try
            {
                var (subject, body) = ChannelFormatter.Email(NotificationView.Parse(payload), options.Value.ConsoleUrl);
                using var message = new MailMessage { From = new MailAddress(smtp.From), Subject = subject, Body = body, IsBodyHtml = false };
                foreach (var recipient in endpoint.Recipients) message.To.Add(recipient);
                using var client = new SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.UseTls, DeliveryMethod = SmtpDeliveryMethod.Network, UseDefaultCredentials = false };
                if (smtp.UsernameRef is { Length: > 0 } username && smtp.PasswordRef is { Length: > 0 } password)
                {
                    client.Credentials = new NetworkCredential(SecretReference.Resolve(username), SecretReference.Resolve(password));
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(options.Value.RequestTimeout);
                await client.SendMailAsync(message, timeout.Token);
                return (null, false);
            }
            catch (SmtpException ex) when ((int)ex.StatusCode is >= 500 and < 600)
            {
                return ($"smtp_{(int)ex.StatusCode}", false);
            }
            catch (SmtpException ex)
            {
                return ((int)ex.StatusCode > 0 ? $"smtp_{(int)ex.StatusCode}" : "smtp_unavailable", true);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Webhook secret", StringComparison.Ordinal))
            {
                return ("secret_unavailable", true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ("timeout", true);
            }
        }

        /// <summary>Exponential backoff from <see cref="WebhookOptions.RetryBase"/>, capped, with ±20 % jitter.</summary>
        internal TimeSpan Backoff(int attempt)
        {
            var delay = Math.Min(options.Value.RetryBase.TotalMilliseconds * Math.Pow(2, attempt - 1), options.Value.RetryMax.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(delay * (0.8 + Random.Shared.NextDouble() * 0.4));
        }

        private static async Task DrainAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[8192];
            var total = 0;
            int read;
            while (total < maxBytes && (read = await body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes - total)), cancellationToken)) > 0)
            {
                total += read;
            }
        }

        /// <summary>
        /// The client: no redirects, no proxy, connect and request timeouts, and a connect callback that resolves the host,
        /// checks every address against <see cref="DestinationPolicy"/> and the current allowlist, and connects only to
        /// a checked address. It runs for every new connection, so DNS is revalidated each time.
        /// </summary>
        private static HttpClient CreateClient(IServiceScopeFactory scopes, WebhookOptions options, Guid tenantId)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                ConnectTimeout = options.ConnectTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(1),
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var host = context.DnsEndPoint.Host;
                    var port = context.DnsEndPoint.Port;
                    IReadOnlyList<IPAddress> addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                        ? [literal]
                        : await Dns.GetHostAddressesAsync(host, cancellationToken);
                    IReadOnlyList<AllowedDestination> allowlist;
                    await using (var scope = scopes.CreateAsyncScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                        allowlist = (await db.WebhookAllowlist.AsNoTracking().Where(a => a.TenantId == tenantId).ToListAsync(cancellationToken))
                            .Select(a => new AllowedDestination(a.Destination, a.Ports)).ToList();
                    }
                    var scheme = context.InitialRequestMessage.RequestUri?.Scheme ?? "";
                    if (DestinationPolicy.Check(scheme, host, port, addresses, allowlist, options.AllowHttpToAllowlisted) is { } reason)
                    {
                        throw new DestinationBlockedException(reason);
                    }
                    var socket = await DestinationPolicy.ConnectAsync(addresses, port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            };
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }

    /// <summary>What a test send did: <see cref="Id"/> is the <c>webhook-id</c> it carried.</summary>
    public sealed record WebhookTestResult(Guid Id, bool Delivered, int? StatusCode, string? Error, int DurationMs);

    /// <summary>The URL behind a chat channel's secret isn't where such a channel may post.</summary>
    internal sealed class ChatDestinationRefusedException : Exception;

    /// <summary>
    /// Loads <c>Webhooks:Endpoints</c> and <c>Webhooks:Allowlist</c> at startup: invalid configuration stops the host;
    /// every endpoint create, update or disable and every allowlist addition or removal writes an audit record
    /// (requirements §8: allowlist changes are audited). The file owns what it lists, taking over a key the API created. What it no longer
    /// lists is disabled or removed as before and handed to the API; what the API created and the file never listed is left alone.
    /// </summary>
    public sealed class WebhookProvisioner(IServiceScopeFactory scopes, IOptions<WebhookOptions> options, IOptions<PlatformOptions> platform,
        TimeProvider clock, ILogger<WebhookProvisioner> logger) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var settings = options.Value;
            var errors = settings.Endpoints.SelectMany(e => e.Validate(settings.AllowHttpToAllowlisted)).ToList();
            errors.AddRange(settings.Endpoints.GroupBy(e => e.Key).Where(g => g.Count() > 1).Select(g => $"webhook '{g.Key}': defined more than once"));
            errors.AddRange(settings.Allowlist.Select(a => DestinationPolicy.ValidateEntry(new AllowedDestination(a.Destination, a.Ports))).OfType<string>());
            if (errors.Count > 0)
            {
                throw new InvalidOperationException("Invalid webhook configuration:\n" + string.Join("\n", errors));
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var tenantId = await db.Tenants.Where(t => t.Key == platform.Value.TenantKey).Select(t => t.Id).SingleAsync(cancellationToken);
            var now = clock.GetUtcNow();
            var changes = 0;
            void Audit(string operation, string targetType, string target, Dictionary<string, string> metadata)
            {
                db.AuditRecords.Add(new AuditRecord
                {
                    TenantId = tenantId, Actor = RuleProvisioner.Actor, Operation = operation, TargetType = targetType, TargetId = target,
                    Outcome = AuditOutcome.Succeeded, Metadata = metadata,
                });
                // The name and the operation only: an endpoint's URL and secret stay out of change events.
                db.ChangeEvents.Add(new ChangeEvent
                {
                    TenantId = tenantId, Kind = ChangeKind.Configuration, Source = ChangeEvent.PlatformSource, Actor = RuleProvisioner.Actor,
                    Summary = $"{targetType} {target}: {operation}", OccurredAt = now, RecordedAt = now,
                });
                changes++;
            }

            var endpoints = await db.WebhookEndpoints.Where(e => e.TenantId == tenantId).ToDictionaryAsync(e => e.Key, cancellationToken);
            foreach (var definition in settings.Endpoints)
            {
                var hash = definition.Hash();
                // The URL host is audited, never the secret or the full URL (which may carry a token in its path).
                var metadata = new Dictionary<string, string> { ["kind"] = definition.Kind.ToString(), ["secretRef"] = definition.SecretRef };
                if (definition.Kind == ChannelKind.Webhook) metadata["host"] = new Uri(definition.Url).Host;
                if (!endpoints.TryGetValue(definition.Key, out var endpoint))
                {
                    db.WebhookEndpoints.Add(new WebhookEndpoint
                    {
                        TenantId = tenantId, Key = definition.Key, Kind = definition.Kind, Url = definition.Url, SecretRef = definition.SecretRef,
                        Events = [.. definition.Events], RuleKeys = [.. definition.RuleKeys], Environments = [.. definition.Environments],
                        Recipients = [.. definition.Recipients], GroupWindowSeconds = definition.GroupWindowSeconds, MinimumSeverity = definition.MinimumSeverity,
                        Enabled = definition.Enabled, DefinitionHash = hash, LoadedAt = now, ManagedBy = ManagedBy.Configuration,
                    });
                    Audit("webhook.create", "webhook", definition.Key, metadata);
                }
                else if (endpoint.DefinitionHash != hash || endpoint.ManagedBy != ManagedBy.Configuration)
                {
                    endpoint.ManagedBy = ManagedBy.Configuration;
                    endpoint.Kind = definition.Kind;
                    endpoint.Environments = [.. definition.Environments];
                    endpoint.Recipients = [.. definition.Recipients];
                    endpoint.GroupWindowSeconds = definition.GroupWindowSeconds;
                    endpoint.Url = definition.Url;
                    endpoint.SecretRef = definition.SecretRef;
                    endpoint.Events = [.. definition.Events];
                    endpoint.RuleKeys = [.. definition.RuleKeys];
                    endpoint.MinimumSeverity = definition.MinimumSeverity;
                    endpoint.Enabled = definition.Enabled;
                    endpoint.DefinitionHash = hash;
                    endpoint.LoadedAt = now;
                    Audit("webhook.update", "webhook", definition.Key, metadata);
                }
            }
            var configured = settings.Endpoints.Select(e => e.Key).ToHashSet();
            foreach (var removed in endpoints.Values.Where(e => e.ManagedBy == ManagedBy.Configuration && !configured.Contains(e.Key)))
            {
                removed.ManagedBy = ManagedBy.Api;
                removed.LoadedAt = now;
                if (removed.Enabled)
                {
                    removed.Enabled = false;
                    Audit("webhook.disable", "webhook", removed.Key, []);
                }
            }

            var allowlist = await db.WebhookAllowlist.Where(a => a.TenantId == tenantId).ToDictionaryAsync(a => a.Destination, cancellationToken);
            foreach (var entry in settings.Allowlist)
            {
                var ports = entry.Ports.Order().ToArray();
                if (!allowlist.TryGetValue(entry.Destination, out var existing))
                {
                    db.WebhookAllowlist.Add(new WebhookAllowlistEntry { TenantId = tenantId, Destination = entry.Destination, Ports = ports, LoadedAt = now, ManagedBy = ManagedBy.Configuration });
                    Audit("webhook.allowlist.add", "webhook_allowlist", entry.Destination, new() { ["ports"] = string.Join(",", ports) });
                }
                else if (!existing.Ports.Order().SequenceEqual(ports) || existing.ManagedBy != ManagedBy.Configuration)
                {
                    existing.ManagedBy = ManagedBy.Configuration;
                    existing.Ports = ports;
                    existing.LoadedAt = now;
                    Audit("webhook.allowlist.update", "webhook_allowlist", entry.Destination, new() { ["ports"] = string.Join(",", ports) });
                }
            }
            var allowed = settings.Allowlist.Select(a => a.Destination).ToHashSet();
            foreach (var removed in allowlist.Values.Where(a => a.ManagedBy == ManagedBy.Configuration && !allowed.Contains(a.Destination)))
            {
                db.WebhookAllowlist.Remove(removed);
                Audit("webhook.allowlist.remove", "webhook_allowlist", removed.Destination, []);
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Webhooks loaded: {Endpoints} endpoints, {Allowlist} allowlist entries, {Changes} changes",
                settings.Endpoints.Count, settings.Allowlist.Count, changes);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public static class WebhookServiceCollectionExtensions
    {
        /// <summary>
        /// Webhook provisioning and the consumer that plans an alert transition's notifications always; the dispatcher when <c>Webhooks:Enabled</c>.
        /// Register after the rules, with the platform's messaging (the mediator, its TransactionBehavior and the <c>local</c> outbox route).
        /// </summary>
        public static IServiceCollection AddPlatformWebhooks(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<WebhookOptions>(configuration.GetSection(WebhookOptions.Section));
            services.AddMessaging().AddLocalConsumer(consumer => consumer.Subscribe<AlertTransitionedIntegrationEvent, AlertTransitionedConsumer>());
            services.AddHostedService<WebhookProvisioner>();
            if (configuration.GetSection(WebhookOptions.Section).GetValue(nameof(WebhookOptions.Enabled), true))
            {
                services.AddSingleton<WebhookDispatcher>();
                services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
                services.AddSingleton<NotificationWorker>();
                services.AddHostedService(sp => sp.GetRequiredService<NotificationWorker>());
            }
            return services;
        }
    }
}
