using Juice.MediatR;
using Juice.Notifications;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Notifications.Embedded;
using Juice.Notifications.Providers;
using Juice.Observability.Webhooks;
using Juice.Observability.Rules.Notifications;
using History = Juice.Notifications.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NotificationsPipeline = Juice.Observability.Data.NotificationsPipeline;
using PlatformNotificationsOptions = Juice.Observability.Data.PlatformNotificationsOptions;

namespace Juice.Observability.Notifications.Migration
{
    /// <summary>A legacy endpoint as the alias answers it: its id (the integration's), its definition, who owns it, when it last changed.</summary>
    public sealed record AliasEndpoint(Guid Id, LegacyEndpoint Definition, string ManagedBy, DateTimeOffset LoadedAt);

    /// <summary>
    /// The webhooks alias (phase-5-plan I69, M46b; decisions on Q-A to Q-D): <c>/webhooks</c> answered from notifications, each endpoint an
    /// integration, its targets and a route owned by the alias.
    /// <list type="bullet">
    /// <item>Reads come from notifications in every mode; an endpoint's id is its integration's, which keeps the legacy endpoint's id.</item>
    /// <item>Writes go in the caller's open platform transaction (<see cref="INotificationsHostScope"/>): in <c>legacy</c> and <c>shadow</c> the
    /// caller writes the legacy row as well, in the same transaction, so both commit or neither does (<see cref="WritesLegacy"/>); in <c>new</c>
    /// only notifications is written, and a new endpoint gets a fresh id.</item>
    /// </list>
    /// </summary>
    public sealed class WebhookAlias(INotificationsHostScope host, NotificationsDbContext notifications, IOptions<PlatformNotificationsOptions> mode,
        NotificationsEmbeddedOptions embedded, IOptions<NotificationsMigrationOptions> migration, IOptions<DeliveryOptions> delivery,
        IOptions<WebhookOptions> webhooks)
    {
        /// <summary>Whether legacy still sends, so the caller writes its rows beside the alias's (<c>legacy</c>, <c>shadow</c>).</summary>
        public bool WritesLegacy => mode.Value.Pipeline != NotificationsPipeline.New;

        private Guid Tenant => embedded.TenantId;

        private EndpointMappingOptions Options => new(delivery.Value, migration.Value.SmtpServer, migration.Value.SmtpFrom);

        /// <summary>Every endpoint the alias holds, by key.</summary>
        public async Task<IReadOnlyList<AliasEndpoint>> ListAsync(IReadOnlyDictionary<Guid, string>? legacyOwners, CancellationToken ct)
        {
            var integrations = await notifications.Integrations.AsNoTracking()
                .Where(i => i.TenantId == Tenant && (i.ManagedBy == AliasVocabulary.WebhooksAlias || i.ManagedBy == AliasVocabulary.AliasConfig)).OrderBy(i => i.Key).ToListAsync(ct);
            var endpoints = new List<AliasEndpoint>();
            foreach (var integration in integrations)
            {
                if (await AnswerAsync(integration, legacyOwners, ct) is { } endpoint)
                {
                    endpoints.Add(endpoint);
                }
            }
            return endpoints;
        }

        /// <summary>
        /// Whether the alias's API holds an endpoint of this id (its integration, as <c>webhooks-alias</c>): false for one it never could map, and
        /// for one the deployment's file declares (<c>alias-config</c>), which legacy's API leaves alone as well. An endpoint the file drops is handed
        /// back to the API by the provisioner (M46c), and is held from then on.
        /// </summary>
        public Task<bool> HoldsAsync(Guid id, CancellationToken ct)
            => notifications.Integrations.AsNoTracking().AnyAsync(i => i.TenantId == Tenant && i.Id == id && i.ManagedBy == AliasVocabulary.WebhooksAlias, ct);

        /// <summary>The endpoint with this id (its integration's), or null.</summary>
        public async Task<AliasEndpoint?> FindAsync(Guid id, IReadOnlyDictionary<Guid, string>? legacyOwners, CancellationToken ct)
            => await notifications.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == Tenant && i.Id == id && (i.ManagedBy == AliasVocabulary.WebhooksAlias || i.ManagedBy == AliasVocabulary.AliasConfig), ct)
                is { } integration ? await AnswerAsync(integration, legacyOwners, ct) : null;

        private async Task<AliasEndpoint?> AnswerAsync(Integration integration, IReadOnlyDictionary<Guid, string>? legacyOwners, CancellationToken ct)
        {
            if (await MigrationPlanner.ReadSetupAsync(notifications, Tenant, integration.Key, ct) is not { } setup || EndpointMapping.Unmap(setup).Value is not { } definition)
            {
                return null;
            }
            // Who owns it as legacy says: the legacy row's owner while legacy rows are written; else the file, if it declares the key.
            // The alias's configuration's rows are the deployment's (M46c), as legacy's file-declared endpoints were.
            var owner = legacyOwners?.GetValueOrDefault(integration.Id)
                ?? (integration.ManagedBy == AliasVocabulary.AliasConfig || webhooks.Value.Endpoints.Any(e => e.Key == integration.Key) ? Data.ManagedBy.Configuration : Data.ManagedBy.Api);
            return new AliasEndpoint(integration.Id, definition, owner, integration.UpdatedAt);
        }

        /// <summary>
        /// Writes an endpoint's notifications rows in the caller's open platform transaction: created with <paramref name="id"/>, or changed in
        /// place (its id kept), the targets it no longer has deleted. What the mapping or notifications refuses comes back, and the caller rolls
        /// its transaction back.
        /// </summary>
        public async Task<IReadOnlyList<MappingProblem>> PutAsync(LegacyEndpoint definition, Guid id, CancellationToken ct)
        {
            var mapped = EndpointMapping.Map(definition, Options);
            if (!mapped.Succeeded)
            {
                return mapped.Problems;
            }
            var problem = await host.RunAsync(async work =>
            {
                var surplus = await work.Db.Integrations.AsNoTracking().Where(i => i.TenantId == Tenant && i.Key == definition.Key)
                    .Join(work.Db.IntegrationTargets.AsNoTracking(), i => new { i.TenantId, i.Id }, t => new { t.TenantId, Id = t.IntegrationId }, (i, t) => t.Key)
                    .ToListAsync(ct);
                surplus.RemoveAll(k => mapped.Value!.Targets.Any(t => t.Key == k));
                return await EndpointWrites.PutAsync(new HostSender(work), Tenant, mapped.Value!, surplus, id, ct);
            }, ct);
            return problem is null ? [] : [problem];
        }

        /// <summary>The keys of the endpoints the alias holds: what an escalation step may name in new (waves §6.4 C).</summary>
        public async Task<IReadOnlySet<string>> EndpointKeysAsync(CancellationToken ct)
            => (await notifications.Integrations.AsNoTracking()
                .Where(i => i.TenantId == Tenant && (i.ManagedBy == AliasVocabulary.WebhooksAlias || i.ManagedBy == AliasVocabulary.AliasConfig))
                .Select(i => i.Key).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        /// <summary>Whether the alias holds a notifications policy of this key.</summary>
        public Task<bool> HoldsPolicyAsync(string key, CancellationToken ct)
            => notifications.EscalationPolicies.AsNoTracking().AnyAsync(p => p.TenantId == Tenant && p.Key == key && p.ManagedBy == AliasVocabulary.WebhooksAlias, ct);

        /// <summary>
        /// A legacy escalation policy's notifications policy (waves §6.4 C), in the caller's transaction: its steps as relative waits naming the
        /// endpoints' routes by key (<see cref="PolicyMapping"/>). The legacy row stays the home of what selects the policy (severity, rules,
        /// environments, enabled), which the producer adapter reads in every mode; in new, these steps are the ones that run.
        /// </summary>
        public async Task<IReadOnlyList<MappingProblem>> PutPolicyAsync(Data.EscalationPolicy policy, CancellationToken ct)
        {
            var mapped = PolicyMapping.Map(LegacyPolicy.From(policy), await EndpointKeysAsync(ct));
            if (!mapped.Succeeded)
            {
                return mapped.Problems;
            }
            var definition = mapped.Value!;
            var problem = await host.RunAsync(work => new HostSender(work).SendAsync(new PutEscalationPolicy(Tenant, definition.Key,
                System.Text.Json.JsonElement.Parse(definition.Steps), definition.StopOnAcknowledge) { ManagedBy = AliasVocabulary.WebhooksAlias }, ct), ct);
            return problem is null ? [] : [problem];
        }

        /// <summary>The allowlist entries the alias holds (new: legacy's table isn't written), with when each last changed.</summary>
        public async Task<IReadOnlyList<(LegacyAllowlistEntry Entry, DateTimeOffset UpdatedAt, string ManagedBy)>> ListAllowlistAsync(CancellationToken ct)
            => [.. (await notifications.Allowlist.AsNoTracking().Where(a => a.TenantId == Tenant && (a.ManagedBy == AliasVocabulary.WebhooksAlias || a.ManagedBy == AliasVocabulary.AliasConfig))
                .OrderBy(a => a.Destination).ToListAsync(ct)).Select(a => (AllowlistMapping.Unmap(new AllowlistDefinition(a.Destination, a.Ports)), a.UpdatedAt,
                    a.ManagedBy == AliasVocabulary.AliasConfig ? Data.ManagedBy.Configuration : Data.ManagedBy.Api))];

        /// <summary>An allowlist entry's notifications row, in the caller's transaction.</summary>
        public async Task<IReadOnlyList<MappingProblem>> PutAllowlistAsync(LegacyAllowlistEntry entry, CancellationToken ct)
        {
            var mapped = AllowlistMapping.Map(entry);
            if (!mapped.Succeeded)
            {
                return mapped.Problems;
            }
            var problem = await host.RunAsync(work => new HostSender(work).SendAsync(
                new PutAllowlistEntry(Tenant, mapped.Value!.Destination, mapped.Value.Ports) { ManagedBy = AliasVocabulary.WebhooksAlias }, ct), ct);
            return problem is null ? [] : [problem];
        }

        /// <summary>Removes an allowlist entry's notifications row, in the caller's transaction.</summary>
        public async Task<IReadOnlyList<MappingProblem>> RemoveAllowlistAsync(string destination, CancellationToken ct)
        {
            var problem = await host.RunAsync(work => new HostSender(work).SendAsync(
                new DeleteAllowlistEntry(Tenant, destination) { ManagedBy = AliasVocabulary.WebhooksAlias }, ct), ct);
            return problem is null ? [] : [problem];
        }

        /// <summary>The alias's commands in the host's transaction, each saved before the next.</summary>
        private sealed class HostSender(NotificationsHostWork work) : ISetupSender
        {
            public async Task<MappingProblem?> SendAsync<T>(IRequest<Outcome<T>> command, CancellationToken ct) => EndpointWrites.Problem(await work.SendAsync(command, ct));
        }

        // ─── Deliveries in new (decision on Q-C) ─────────────────────────────────

        /// <summary>
        /// A notifications delivery in legacy's words (Q-C): <c>Pending</c> and <c>Retrying</c> (and sending) are <c>Pending</c>; <c>Delivered</c>
        /// and <c>Accepted</c> are <c>Delivered</c>; <c>Failed</c>, <c>Expired</c>, <c>Cancelled</c> and <c>Bounced</c> are <c>Failed</c>, the
        /// reason kept; a dry run's <c>WouldSend</c> (shadow only) is <c>Dropped</c>: nothing went out.
        /// </summary>
        public static Data.DeliveryStatus LegacyStatus(DeliveryStatus status) => status switch
        {
            DeliveryStatus.Pending or DeliveryStatus.Sending or DeliveryStatus.Retrying => Data.DeliveryStatus.Pending,
            DeliveryStatus.Delivered or DeliveryStatus.Accepted => Data.DeliveryStatus.Delivered,
            DeliveryStatus.WouldSend => Data.DeliveryStatus.Dropped,
            _ => Data.DeliveryStatus.Failed,
        };

        /// <summary>The notifications statuses one legacy status stands for, to filter by it.</summary>
        public static IReadOnlyList<DeliveryStatus> StatusesOf(Data.DeliveryStatus legacy)
            => [.. Enum.GetValues<DeliveryStatus>().Where(s => LegacyStatus(s) == legacy)];

        /// <summary>An endpoint's deliveries in new, newest first, with their attempts, the event and the alert from their request.</summary>
        public async Task<IReadOnlyList<AliasDelivery>> DeliveriesAsync(Guid endpointId, Data.DeliveryStatus? status, int take, Guid? before, CancellationToken ct,
            Guid? only = null)
        {
            var query = notifications.Deliveries.AsNoTracking().Where(d => d.TenantId == Tenant && d.IntegrationId == endpointId && (only == null || d.Id == only));
            if (status is { } legacy)
            {
                var statuses = StatusesOf(legacy).ToArray();
                query = query.Where(d => statuses.Contains(d.Status));
            }
            if (before is { } id)
            {
                query = query.Where(d => d.Id < id);
            }
            var rows = await query.OrderByDescending(d => d.Id).Take(take).ToListAsync(ct);
            var ids = rows.Select(d => d.Id).ToList();
            var attempts = (await notifications.Attempts.AsNoTracking().Where(a => a.TenantId == Tenant && ids.Contains(a.DeliveryId)).OrderBy(a => a.Number).ToListAsync(ct))
                .ToLookup(a => a.DeliveryId);
            var carried = (await (from i in notifications.DeliveryItems.AsNoTracking()
                                  where i.TenantId == Tenant && ids.Contains(i.DeliveryId)
                                  join q in notifications.Requests.AsNoTracking() on new { i.TenantId, Id = i.RequestId } equals new { q.TenantId, q.Id }
                                  join r in notifications.Recipients.AsNoTracking() on new { i.TenantId, Id = i.RecipientId } equals new { r.TenantId, r.Id }
                                  select new { i.DeliveryId, q.Labels, q.Data, r.EscalationStep }).ToListAsync(ct))
                .GroupBy(x => x.DeliveryId).ToDictionary(g => g.Key, g => g.First());
            return [.. rows.Select(d =>
            {
                var item = carried.GetValueOrDefault(d.Id);
                var labels = item?.Labels ?? [];
                var step = item?.EscalationStep ?? 0;
                var @event = step > 0 ? NotificationPlan.Escalated : labels.GetValueOrDefault("event") ?? "";
                var last = attempts[d.Id].LastOrDefault();
                return new AliasDelivery(d.Id, AlertId(item?.Data), @event, LegacyStatus(d.Status), d.Attempts, d.CreatedAt, d.FirstAttemptAt,
                    d.Status is DeliveryStatus.Delivered or DeliveryStatus.Accepted ? d.CompletedAt : null,
                    LegacyStatus(d.Status) == Data.DeliveryStatus.Pending ? d.NextAt : null, last?.StatusCode, d.LastError,
                    [.. attempts[d.Id].Select(a => new AliasAttempt(a.Number, a.At, a.StatusCode, a.Error, a.DurationMs))], step,
                    labels.GetValueOrDefault("request") is { } request && Guid.TryParse(request, out var requestId) ? requestId : null);
            })];
        }

        /// <summary>The alert a request's data names (<c>alert.id</c>, today's payload), or null.</summary>
        private static Guid? AlertId(string? data)
        {
            if (data is null)
            {
                return null;
            }
            using var document = System.Text.Json.JsonDocument.Parse(data);
            return document.RootElement.TryGetProperty("alert", out var alert) && alert.ValueKind == System.Text.Json.JsonValueKind.Object
                && alert.TryGetProperty("id", out var id) && id.TryGetGuid(out var guid) ? guid : null;
        }

        /// <summary>Per endpoint in new: failed deliveries since <paramref name="since"/>, and those still waiting.</summary>
        public async Task<IReadOnlyDictionary<Guid, (int Failures, int Waiting)>> CountsAsync(DateTimeOffset since, CancellationToken ct)
        {
            var waiting = StatusesOf(Data.DeliveryStatus.Pending).ToArray();
            var failed = StatusesOf(Data.DeliveryStatus.Failed).ToArray();
            var counts = await notifications.Deliveries.AsNoTracking()
                .Where(d => d.TenantId == Tenant && (waiting.Contains(d.Status) || failed.Contains(d.Status) && d.CreatedAt >= since))
                .GroupBy(d => new { d.IntegrationId, Waiting = waiting.Contains(d.Status) }).Select(g => new { g.Key.IntegrationId, g.Key.Waiting, Count = g.Count() })
                .ToListAsync(ct);
            return counts.GroupBy(c => c.IntegrationId).ToDictionary(g => g.Key,
                g => (g.Where(c => !c.Waiting).Sum(c => c.Count), g.Where(c => c.Waiting).Sum(c => c.Count)));
        }

        /// <summary>
        /// A test notification sent now through the endpoint's integration (Q-C, in <c>new</c>): legacy's test payload in the channel's content, to
        /// each of its targets, through the channel's own provider with its destination policy and signing, and nothing stored or retried. Null
        /// when the endpoint has no integration. Delivered only if every target took it; otherwise the first failure's status and error.
        /// </summary>
        public async Task<Juice.Observability.Webhooks.WebhookTestResult?> TestAsync(IServiceProvider services, Guid platformTenantId, Guid endpointId,
            string requestedBy, CancellationToken ct)
        {
            var integration = await notifications.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == Tenant && i.Id == endpointId, ct);
            if (integration is null)
            {
                return null;
            }
            var id = Guid.CreateVersion7();
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Juice.Observability.Webhooks.WebhookTestResult Result(bool delivered, int? status, string? error)
                => new(id, delivered, status, error, (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            var provider = services.GetServices<INotificationProvider>().FirstOrDefault(p => p.Channel == integration.Channel && p.Kind == integration.Kind);
            if (provider is null)
            {
                return Result(false, null, $"no provider sends {integration.Channel}/{integration.Kind}");
            }
            var consoleUrl = services.GetService<IOptions<Juice.Observability.Webhooks.WebhookOptions>>()?.Value.ConsoleUrl;
            var payload = LegacyDelivery.TestPayload(id, platformTenantId, integration.Key, requestedBy, DateTimeOffset.UtcNow);
            var content = integration.Channel switch
            {
                Channels.Webhook => System.Text.Json.JsonSerializer.Serialize(new Juice.Notifications.Rendering.WebhookContent("application/json", payload)),
                Channels.Email => Email(payload, consoleUrl),
                _ => Chat(integration.Kind, payload, consoleUrl),
            };
            var secrets = services.GetService<ISecretResolver>();
            async Task<string> ResolveAsync(string reference) => secrets is not null ? await secrets.ResolveAsync(reference, ct) : SecretReferences.Resolve(reference);
            var targets = await notifications.IntegrationTargets.AsNoTracking().Where(t => t.TenantId == Tenant && t.IntegrationId == integration.Id).OrderBy(t => t.Key).ToListAsync(ct);
            if (targets.Count == 0)
            {
                return Result(false, null, "the endpoint has no destination");
            }
            try
            {
                var secret = integration.SecretRef is { } reference ? await ResolveAsync(reference) : null;
                foreach (var target in targets)
                {
                    var destination = target.AddressRef is { } addressRef ? await ResolveAsync(addressRef) : target.Address!;
                    var sent = await provider.SendAsync(new RenderedDelivery(id, Tenant, integration.Channel, content, destination, 1, NotificationPriority.Normal, false),
                        new IntegrationContext(integration.Id, integration.Key, integration.Kind, integration.Settings, secret), ct);
                    if (sent.Outcome is not (AttemptOutcome.Delivered or AttemptOutcome.Accepted))
                    {
                        return Result(false, sent.StatusCode, sent.Error ?? sent.Category?.ToString() ?? sent.Outcome.ToString());
                    }
                    if (target == targets[^1])
                    {
                        return Result(true, sent.StatusCode, null);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or SecretUnavailableException)
            {
                // A secret that isn't set: nothing was sent.
                return Result(false, null, ex.Message);
            }
            return Result(false, null, "the endpoint has no destination");
        }

        private static string Chat(string kind, string payload, string? consoleUrl)
        {
            var (title, text) = LegacyDelivery.Chat(kind == "teams" ? Data.ChannelKind.Teams : Data.ChannelKind.Slack, payload, consoleUrl);
            return System.Text.Json.JsonSerializer.Serialize(new Juice.Notifications.Rendering.ChatContent(title, text, null, null));
        }

        private static string Email(string payload, string? consoleUrl)
        {
            var (subject, text) = LegacyDelivery.Email(payload, consoleUrl);
            return System.Text.Json.JsonSerializer.Serialize(new Juice.Notifications.Rendering.EmailContent(subject, text, $"<pre>{System.Net.WebUtility.HtmlEncode(text)}</pre>"));
        }

        /// <summary>A failed delivery of the endpoint sent once more (notifications' retry, its own transaction), or why not.</summary>
        public async Task<Outcome<History.RetriedView>?> RetryAsync(IMediator mediator, Guid endpointId, Guid deliveryId, CancellationToken ct)
        {
            if (!await notifications.Deliveries.AnyAsync(d => d.TenantId == Tenant && d.IntegrationId == endpointId && d.Id == deliveryId, ct))
            {
                return null;
            }
            using var context = NotificationsMessageContext.Begin("webhooks-alias");
            return await mediator.Send(new History.RetryDelivery(Tenant, deliveryId), ct);
        }
    }

    public sealed record AliasAttempt(int Number, DateTimeOffset At, int? StatusCode, string? Error, int DurationMs);

    /// <summary>A delivery of an endpoint as legacy's API shows one (Q-C): no group or silence of legacy's own, the step from the recipient.</summary>
    public sealed record AliasDelivery(Guid Id, Guid? AlertId, string Event, Data.DeliveryStatus Status, int Attempts, DateTimeOffset CreatedAt,
        DateTimeOffset? FirstAttemptAt, DateTimeOffset? DeliveredAt, DateTimeOffset? NextAttemptAt, int? LastStatusCode, string? LastError,
        IReadOnlyList<AliasAttempt> History, int EscalationStep, Guid? ActionRequestId);
}
