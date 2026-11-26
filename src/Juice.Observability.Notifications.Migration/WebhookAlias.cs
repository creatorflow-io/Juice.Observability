using Juice.MediatR;
using Juice.Notifications;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Notifications.Embedded;
using Juice.Notifications.Providers;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
                .Where(i => i.TenantId == Tenant && i.ManagedBy == AliasVocabulary.WebhooksAlias).OrderBy(i => i.Key).ToListAsync(ct);
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

        /// <summary>The endpoint with this id (its integration's), or null.</summary>
        public async Task<AliasEndpoint?> FindAsync(Guid id, IReadOnlyDictionary<Guid, string>? legacyOwners, CancellationToken ct)
            => await notifications.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == Tenant && i.Id == id && i.ManagedBy == AliasVocabulary.WebhooksAlias, ct)
                is { } integration ? await AnswerAsync(integration, legacyOwners, ct) : null;

        private async Task<AliasEndpoint?> AnswerAsync(Integration integration, IReadOnlyDictionary<Guid, string>? legacyOwners, CancellationToken ct)
        {
            if (await MigrationPlanner.ReadSetupAsync(notifications, Tenant, integration.Key, ct) is not { } setup || EndpointMapping.Unmap(setup).Value is not { } definition)
            {
                return null;
            }
            // Who owns it as legacy says: the legacy row's owner while legacy rows are written; else the file, if it declares the key.
            var owner = legacyOwners?.GetValueOrDefault(integration.Id)
                ?? (webhooks.Value.Endpoints.Any(e => e.Key == integration.Key) ? Data.ManagedBy.Configuration : Data.ManagedBy.Api);
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
    }
}
