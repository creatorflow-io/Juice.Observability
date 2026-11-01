using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    }

    public sealed partial class EndpointDefinition
    {
        public string Key { get; set; } = "";
        public string Url { get; set; } = "";
        public string SecretRef { get; set; } = "";
        public List<string> Events { get; set; } = [WebhookOutbox.Firing, WebhookOutbox.Resolved];
        public List<string> RuleKeys { get; set; } = [];
        public AlertSeverity MinimumSeverity { get; set; } = AlertSeverity.Info;
        public bool Enabled { get; set; } = true;

        public IEnumerable<string> Validate(bool allowHttp)
        {
            var at = $"webhook '{Key}'";
            if (!KeyPattern().IsMatch(Key)) yield return $"{at}: Key must match {KeyPattern()}";
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0)
                yield return $"{at}: Url must be an absolute https URL without credentials";
            else if (uri.Scheme == "http" && !allowHttp)
                yield return $"{at}: Url must be https (AllowHttpToAllowlisted is off)";
            if (!SecretReference.IsValid(SecretRef)) yield return $"{at}: SecretRef must be env:NAME or file:/path";
            if (Events.Count == 0 || Events.Any(e => e is not (WebhookOutbox.Firing or WebhookOutbox.Resolved)))
                yield return $"{at}: Events are {WebhookOutbox.Firing} and/or {WebhookOutbox.Resolved}";
        }

        public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));

        [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
        private static partial Regex KeyPattern();
    }

    public sealed class AllowlistDefinition
    {
        public string Destination { get; set; } = "";
        public List<int> Ports { get; set; } = [];
    }

    /// <summary>
    /// Writes one delivery per matching endpoint when an alert fires or resolves, in the evaluation's transaction
    /// (<see cref="IAlertNotifier"/>). The payload is fixed here, so every attempt sends and signs the same bytes.
    /// </summary>
    public sealed class WebhookOutbox(IOptions<PlatformOptions> platform, TimeProvider clock) : IAlertNotifier
    {
        public const string Firing = "alert.firing";
        public const string Resolved = "alert.resolved";

        public void OnTransition(PlatformDbContext db, Rule rule, Alert alert, AlertTransition transition)
        {
            var type = transition.ToState == AlertState.Firing ? Firing : Resolved;
            var endpoints = db.WebhookEndpoints.AsNoTracking().Where(e => e.TenantId == alert.TenantId && e.Enabled).ToList()
                .Where(e => e.Events.Contains(type) && (e.RuleKeys.Length == 0 || e.RuleKeys.Contains(rule.Key)) && alert.Severity >= e.MinimumSeverity);
            foreach (var endpoint in endpoints)
            {
                var id = Guid.CreateVersion7();
                db.WebhookDeliveries.Add(new WebhookDelivery
                {
                    Id = id, TenantId = alert.TenantId, EndpointId = endpoint.Id, AlertId = alert.Id, TransitionId = transition.Id,
                    Event = type, Payload = Payload(id, type, rule, alert, transition), CreatedAt = clock.GetUtcNow(),
                    NextAttemptAt = clock.GetUtcNow(),
                });
            }
        }

        private string Payload(Guid id, string type, Rule rule, Alert alert, AlertTransition transition)
            => JsonSerializer.Serialize(new
            {
                id,
                type,
                occurredAt = transition.At,
                tenant = platform.Value.TenantKey,
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
    /// touches alert state.
    /// </summary>
    public sealed class WebhookDispatcher(IServiceScopeFactory scopes, IOptions<WebhookOptions> options, TimeProvider clock,
        ILogger<WebhookDispatcher> logger) : BackgroundService
    {
        private static readonly Meter Meter = new("Juice.Observability");
        private static readonly Counter<long> Attempts = Meter.CreateCounter<long>("juice.observability.webhooks.attempts", "{attempt}",
            "Webhook send attempts, by outcome (delivered, retry, failed, blocked)");
        private static readonly Histogram<double> DispatchDelay = Meter.CreateHistogram<double>("juice.observability.webhooks.dispatch_delay", "s",
            "From the alert transition to the first attempt (requirements §9: p95 ≤ 30 s)");

        private readonly Lazy<HttpClient> _client = new(() => CreateClient(scopes, options.Value));

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

            var number = delivery.Attempts + 1;
            var started = Stopwatch.GetTimestamp();
            int? status = null;
            string? error = null;
            var retry = false;
            if (!endpoint.Enabled)
            {
                error = "endpoint_disabled";
            }
            else
            {
                try
                {
                    var timestamp = now.ToUnixTimeSeconds();
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
                    {
                        Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json"),
                    };
                    request.Headers.Add(WebhookSigner.IdHeader, delivery.Id.ToString());
                    request.Headers.Add(WebhookSigner.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
                    request.Headers.Add(WebhookSigner.SignatureHeader,
                        WebhookSigner.Sign(SecretReference.Resolve(endpoint.SecretRef), delivery.Id.ToString(), timestamp, delivery.Payload));
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(options.Value.RequestTimeout);
                    using var response = await _client.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    status = (int)response.StatusCode;
                    await DrainAsync(response, options.Value.MaxResponseBytes, timeout.Token);
                    retry = status is 408 or 429 or >= 500;
                    if (!response.IsSuccessStatusCode) error = $"status_{status}";
                }
                catch (HttpRequestException ex) when (ex.InnerException is DestinationBlockedException blocked)
                {
                    error = blocked.Reason;
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("Webhook secret", StringComparison.Ordinal))
                {
                    error = "secret_unavailable";
                    retry = true;   // a secret mounted late should not lose the notification
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException && !cancellationToken.IsCancellationRequested)
                {
                    error = ex is OperationCanceledException ? "timeout" : "network_error";
                    retry = true;
                }
            }

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
        private static HttpClient CreateClient(IServiceScopeFactory scopes, WebhookOptions options)
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
                        allowlist = (await db.WebhookAllowlist.AsNoTracking().ToListAsync(cancellationToken))
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

    /// <summary>
    /// Loads <c>Webhooks:Endpoints</c> and <c>Webhooks:Allowlist</c> at startup: invalid configuration stops the host;
    /// every endpoint create, update or disable and every allowlist addition or removal writes an audit record
    /// (requirements §8: allowlist changes are audited).
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
                changes++;
            }

            var endpoints = await db.WebhookEndpoints.Where(e => e.TenantId == tenantId).ToDictionaryAsync(e => e.Key, cancellationToken);
            foreach (var definition in settings.Endpoints)
            {
                var hash = definition.Hash();
                // The URL host is audited, never the secret or the full URL (which may carry a token in its path).
                var metadata = new Dictionary<string, string> { ["host"] = new Uri(definition.Url).Host, ["secretRef"] = definition.SecretRef };
                if (!endpoints.TryGetValue(definition.Key, out var endpoint))
                {
                    db.WebhookEndpoints.Add(new WebhookEndpoint
                    {
                        TenantId = tenantId, Key = definition.Key, Url = definition.Url, SecretRef = definition.SecretRef,
                        Events = [.. definition.Events], RuleKeys = [.. definition.RuleKeys], MinimumSeverity = definition.MinimumSeverity,
                        Enabled = definition.Enabled, DefinitionHash = hash, LoadedAt = now,
                    });
                    Audit("webhook.create", "webhook", definition.Key, metadata);
                }
                else if (endpoint.DefinitionHash != hash)
                {
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
            foreach (var removed in endpoints.Values.Where(e => e.Enabled && !configured.Contains(e.Key)))
            {
                removed.Enabled = false;
                removed.LoadedAt = now;
                Audit("webhook.disable", "webhook", removed.Key, []);
            }

            var allowlist = await db.WebhookAllowlist.Where(a => a.TenantId == tenantId).ToDictionaryAsync(a => a.Destination, cancellationToken);
            foreach (var entry in settings.Allowlist)
            {
                var ports = entry.Ports.Order().ToArray();
                if (!allowlist.TryGetValue(entry.Destination, out var existing))
                {
                    db.WebhookAllowlist.Add(new WebhookAllowlistEntry { TenantId = tenantId, Destination = entry.Destination, Ports = ports, LoadedAt = now });
                    Audit("webhook.allowlist.add", "webhook_allowlist", entry.Destination, new() { ["ports"] = string.Join(",", ports) });
                }
                else if (!existing.Ports.Order().SequenceEqual(ports))
                {
                    existing.Ports = ports;
                    existing.LoadedAt = now;
                    Audit("webhook.allowlist.update", "webhook_allowlist", entry.Destination, new() { ["ports"] = string.Join(",", ports) });
                }
            }
            var allowed = settings.Allowlist.Select(a => a.Destination).ToHashSet();
            foreach (var removed in allowlist.Values.Where(a => !allowed.Contains(a.Destination)))
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
        /// <summary>Webhook provisioning and the outbox always; the dispatcher when <c>Webhooks:Enabled</c>. Register after the rules.</summary>
        public static IServiceCollection AddPlatformWebhooks(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<WebhookOptions>(configuration.GetSection(WebhookOptions.Section));
            services.AddSingleton<IAlertNotifier, WebhookOutbox>();
            services.AddHostedService<WebhookProvisioner>();
            if (configuration.GetSection(WebhookOptions.Section).GetValue(nameof(WebhookOptions.Enabled), true))
            {
                services.AddSingleton<WebhookDispatcher>();
                services.AddHostedService(sp => sp.GetRequiredService<WebhookDispatcher>());
            }
            return services;
        }
    }
}
