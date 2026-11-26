using System.Security.Claims;
using System.Text.Json;
using Juice.EF.Extensions;
using Juice.MediatR;
using Juice.Notifications;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Notifications.Embedded;
using Juice.Notifications.Providers;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationsEscalationPolicy = Juice.Notifications.Data.EscalationPolicy;

namespace Juice.Observability.Notifications.Migration
{
    /// <summary>What the migration needs that neither side's settings say: the SMTP server an email endpoint sends through, and its sender.</summary>
    public sealed class NotificationsMigrationOptions
    {
        /// <summary>The name, in notifications' <c>Delivery:SmtpServers</c>, of the server legacy <c>Webhooks:Smtp</c> is.</summary>
        public string? SmtpServer { get; set; }

        /// <summary>The sender, legacy <c>Webhooks:Smtp:From</c>.</summary>
        public string? SmtpFrom { get; set; }
    }

    /// <summary>
    /// The legacy setup of one platform tenant, read once: what the migration maps. <see cref="TenantId"/> is the platform's row (legacy rows
    /// refer to it); <see cref="TenantKey"/> is its key, which is the notifications tenant's Id (the tenant pattern: a deployment renamed from an
    /// old name keeps its row's Id, so the two can differ).
    /// </summary>
    public sealed record LegacySetup(Guid TenantId, string TenantKey, IReadOnlyList<WebhookEndpoint> Endpoints, IReadOnlyList<Data.EscalationPolicy> Policies,
        IReadOnlyList<WebhookAllowlistEntry> Allowlist)
    {
        public static async Task<LegacySetup> ReadAsync(PlatformDbContext db, Guid tenantId, CancellationToken ct)
            => new(tenantId,
                await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Key).SingleOrDefaultAsync(ct) ?? "",
                await db.WebhookEndpoints.AsNoTracking().Where(e => e.TenantId == tenantId).OrderBy(e => e.Key).ToListAsync(ct),
                await db.EscalationPolicies.AsNoTracking().Where(p => p.TenantId == tenantId).OrderBy(p => p.Key).ToListAsync(ct),
                await db.WebhookAllowlist.AsNoTracking().Where(a => a.TenantId == tenantId).OrderBy(a => a.Destination).ToListAsync(ct));
    }

    public enum MigrationSubject { Endpoint, Policy, Allowlist }

    /// <summary>
    /// What the migration does with an item. <see cref="PendingFeature"/>: the mapping is complete, but the pinned notifications alpha can't
    /// write it yet (a route's digest before M46-1, decision G2); it isn't written, and isn't a conflict.
    /// </summary>
    public enum MigrationAction { Create, Update, Unchanged, Conflict, PendingFeature }

    /// <summary>One legacy endpoint, policy or allowlist entry, and what the migration does with it.</summary>
    public sealed record MigrationItem(MigrationSubject Subject, string Key, MigrationAction Action, IReadOnlyList<MappingProblem> Problems);

    /// <summary>
    /// The report (phase-5-plan I70): per item, <c>create</c>, <c>update</c>, <c>unchanged</c> or <c>conflict</c> with why. After an apply,
    /// <see cref="Applied"/>: every item that wasn't a conflict was written, in one transaction.
    /// </summary>
    public sealed record MigrationReport(Guid TenantId, bool Applied, IReadOnlyList<MigrationItem> Items)
    {
        public bool HasConflicts => Items.Any(i => i.Action == MigrationAction.Conflict);

        public bool HasChanges => Items.Any(i => i.Action is MigrationAction.Create or MigrationAction.Update);
    }

    /// <summary>
    /// The migration tool's core (phase-5-plan I70, waves §5.3): maps a tenant's legacy setup, compares it with the rows the alias owns in
    /// notifications (<see cref="AliasVocabulary.WebhooksAlias"/>), reports, and applies.
    /// <list type="bullet">
    /// <item>Keyed by endpoint key (an allowlist entry by its destination), so a second run finds everything unchanged.</item>
    /// <item>A key held by a row the alias doesn't own (the API's or the configuration's) is a conflict: reported, never overwritten. So are
    /// overlapping escalation policies (Q1) and steps out of time order (Q3), and anything else the mapping refuses.</item>
    /// <item>Apply writes every item that isn't a conflict through notifications' own commands, in one <see cref="NotificationsDbContext"/>
    /// transaction for the tenant (the commands join it: Juice's TransactionBehavior leaves a managed context to its owner). Any write that is
    /// refused rolls everything back. The legacy rows are never changed.</item>
    /// </list>
    /// The platform tenant only: notifications embedded in Observability has that one tenant.
    /// </summary>
    public sealed class MigrationPlanner(NotificationsDbContext db, IMediator mediator, IOptions<DeliveryOptions> delivery, NotificationsEmbeddedOptions embedded,
        IOptions<NotificationsMigrationOptions> options, ILogger<MigrationPlanner> logger)
    {
        /// <summary>The message source of the migration's writes, and the actor its audit entries name when no user runs it (the host command).</summary>
        public const string Source = "webhooks-migration";


        /// <summary>
        /// Whether this run is the alias's provisioner's (at start, from the deployment's file): what legacy's configuration declares is then
        /// written as <see cref="AliasVocabulary.AliasConfig"/>. The migration tool (its endpoint and host command) writes only the alias's own,
        /// and reports a configuration row in its way as a conflict.
        /// </summary>
        internal bool AsProvisioner { get; set; }

        /// <summary>The owner a legacy row's notifications rows are written as.</summary>
        private string OwnerOf(string legacyOwner)
            => AsProvisioner && legacyOwner == Juice.Observability.Data.ManagedBy.Configuration ? AliasVocabulary.AliasConfig : AliasVocabulary.WebhooksAlias;

        /// <summary>For the tests of a failure part-way: throws after this many writes.</summary>
        internal int? FailAfterWrites { get; set; }

        private EndpointMappingOptions MappingOptions => new(delivery.Value, options.Value.SmtpServer, options.Value.SmtpFrom);

        /// <summary>The notifications tenant: the platform tenant's Id, which embedded notifications has (its one tenant).</summary>
        private Guid Tenant => embedded.TenantId;

        private NotificationsDbContext Db => db;

        /// <summary>Whether the pinned notifications alpha writes a route's digestSeconds (M46-1, decision G2). True, and passed to PutRoute, once it is pinned.</summary>
        internal static bool RouteDigestSupported => true;

        /// <summary>
        /// Whether the pinned notifications takes a route by its key in an escalation step (M46-0), as every migrated policy's steps name their
        /// endpoints. Asked of its own step rules, not guessed from its version: before M46-0 they refuse such a step, and the apply would roll
        /// back.
        /// </summary>
        internal static bool StepsNameRoutes { get; } = EscalationRules.Parse(
            JsonDocument.Parse("""[{ "afterMinutes": 0, "recipients": [{ "kind": "route", "key": "probe" }] }]""").RootElement, null).Errors.Count == 0;

        public async Task<MigrationReport> ReportAsync(LegacySetup legacy, CancellationToken ct)
        {
            Check(legacy);
            var plan = Plan(legacy, await ReadAsync(Tenant, ct));
            return new MigrationReport(legacy.TenantId, false, plan.Items);
        }

        /// <summary>
        /// Applies the plan. The pipeline's audit entries name <paramref name="actor"/>, the platform's name for the user who runs it, or
        /// <see cref="Source"/> when no user does (the host command).
        /// </summary>
        public async Task<MigrationReport> ApplyAsync(LegacySetup legacy, CancellationToken ct, string? actor = null)
        {
            Check(legacy);
            db.SetUser(new ClaimsPrincipal(new ClaimsIdentity([new System.Security.Claims.Claim(ClaimTypes.NameIdentifier, string.IsNullOrEmpty(actor) ? Source : actor)], Source)));
            using var context = NotificationsMessageContext.Begin(Source);
            // The steps of Juice's TransactionBehavior around several commands, as notifications' provisioning does: the context is managed, so
            // each command's behavior leaves the transaction to this; each step is saved as it goes (a route's checks read the targets saved
            // before it), and the audit events go out once, before the commit.
            db.BeginManage();
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({"juice-observability:webhooks-alias:" + Tenant}))", ct);
                // Planned again under the lock: what the report saw may have changed.
                var plan = Plan(legacy, await ReadAsync(Tenant, ct));
                var writes = 0;
                foreach (var write in plan.Writes)
                {
                    if (await write.Run(this, ct) is { } refused)
                    {
                        await transaction.RollbackAsync(ct);
                        db.ChangeTracker.Clear();
                        db.ClearEvents();
                        logger.LogWarning("Notifications migration of tenant {Tenant} rolled back: {Item} was refused: {Problem}", legacy.TenantId, write.Item, refused.Message);
                        return new MigrationReport(legacy.TenantId, false, [.. plan.Items.Select(i => (i.Subject, i.Key) == write.Item
                            ? i with { Action = MigrationAction.Conflict, Problems = [.. i.Problems, refused] } : i)]);
                    }
                    await db.SaveChangesAsync(ct);
                    if (++writes == FailAfterWrites)
                    {
                        throw new InvalidOperationException($"Failing after {writes} writes, as the test asked.");
                    }
                }
                await mediator.DispatchEventsUntilStableAsync(db, logger, cancellationToken: ct);
                await db.CommitTransactionAsync(transaction.TransactionId, ct);
                db.ClearEvents();
                if (plan.Writes.Count > 0)
                {
                    logger.LogInformation("Notifications migration of tenant {Tenant}: {Writes} writes", legacy.TenantId, plan.Writes.Count);
                }
                return new MigrationReport(legacy.TenantId, true, plan.Items);
            }
            catch
            {
                // The rolled-back writes' audit entries too: they outlive ChangeTracker.Clear, and a later apply in this scope would report them.
                db.ChangeTracker.Clear();
                db.ClearEvents();
                throw;
            }
            finally
            {
                db.EndManage();
            }
        }

        private void Check(LegacySetup legacy)
        {
            if (!string.Equals(legacy.TenantKey, embedded.TenantId.ToString("D"), StringComparison.Ordinal))
            {
                throw new ArgumentException($"The migration runs for the platform tenant {embedded.TenantId} only, not {legacy.TenantId} ({legacy.TenantKey}).", nameof(legacy));
            }
        }

        // ─── What is there ────────────────────────────────────────────────────

        /// <summary>The tenant's notifications rows, every owner's: a key the alias doesn't own is a conflict.</summary>
        internal sealed record Current(Dictionary<string, Integration> Integrations, Dictionary<string, IntegrationTarget> Targets, Dictionary<Guid, string> IntegrationKeys,
            Dictionary<string, Route> Routes, Dictionary<string, NotificationsEscalationPolicy> Policies, Dictionary<string, AllowlistEntry> Allowlist);

        private async Task<Current> ReadAsync(Guid tenantId, CancellationToken ct)
        {
            var integrations = await db.Integrations.AsNoTracking().Where(i => i.TenantId == tenantId).ToListAsync(ct);
            return new Current(
                integrations.ToDictionary(i => i.Key, StringComparer.Ordinal),
                await db.IntegrationTargets.AsNoTracking().Where(t => t.TenantId == tenantId).ToDictionaryAsync(t => t.Key, StringComparer.Ordinal, ct),
                integrations.ToDictionary(i => i.Id, i => i.Key),
                await db.Routes.AsNoTracking().Where(r => r.TenantId == tenantId).ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, ct),
                await db.EscalationPolicies.AsNoTracking().Where(p => p.TenantId == tenantId).ToDictionaryAsync(p => p.Key, StringComparer.Ordinal, ct),
                await db.Allowlist.AsNoTracking().Where(a => a.TenantId == tenantId).ToDictionaryAsync(a => a.Destination, StringComparer.Ordinal, ct));
        }

        // ─── The plan ─────────────────────────────────────────────────────────

        /// <summary>One command of the apply, for one item; returns the problem when notifications refuses it.</summary>
        internal sealed record Write((MigrationSubject, string) Item, Func<MigrationPlanner, CancellationToken, Task<MappingProblem?>> Run);

        internal sealed record PlanResult(IReadOnlyList<MigrationItem> Items, IReadOnlyList<Write> Writes);

        private static MappingProblem Taken(string what, string key, string owner)
            => new(ProblemCodes.KeyTaken, $"The {what} '{key}' exists in notifications, managed by {owner}: change or delete it there, or give the legacy one another key.");

        internal PlanResult Plan(LegacySetup legacy, Current current)
        {
            var items = new List<MigrationItem>();
            var writes = new List<Write>();
            var tenant = Tenant;

            // Allowlist first: a webhook target may be on a private address only the allowlist lets it reach.
            var destinations = legacy.Allowlist.Select(a => (Legacy: a, Mapped: AllowlistMapping.Map(LegacyAllowlistEntry.From(a)))).ToList();
            foreach (var (entry, mapped) in destinations)
            {
                var item = (Subject: MigrationSubject.Allowlist, Key: entry.Destination);
                if (!mapped.Succeeded)
                {
                    items.Add(new(item.Subject, item.Key, MigrationAction.Conflict, mapped.Problems));
                    continue;
                }
                var row = mapped.Value!;
                if (destinations.Count(d => d.Mapped.Value?.Destination == row.Destination) > 1)
                {
                    items.Add(new(item.Subject, item.Key, MigrationAction.Conflict,
                        [new(ProblemCodes.KeyTaken, $"Several legacy entries are the destination {row.Destination}; keep one.")]));
                    continue;
                }
                var owner = OwnerOf(entry.ManagedBy);
                current.Allowlist.TryGetValue(row.Destination, out var existing);
                if (existing is not null && !AliasVocabulary.Writes(owner, existing.ManagedBy))
                {
                    items.Add(new(item.Subject, item.Key, MigrationAction.Conflict, [Taken("allowlist entry", row.Destination, existing.ManagedBy)]));
                    continue;
                }
                var action = existing is null ? MigrationAction.Create
                    : existing.Ports.Order().SequenceEqual(row.Ports) && existing.ManagedBy == owner ? MigrationAction.Unchanged : MigrationAction.Update;
                items.Add(new(item.Subject, item.Key, action, []));
                if (action != MigrationAction.Unchanged)
                {
                    writes.Add(new(item, (p, ct) => p.PutAsync(new PutAllowlistEntry(tenant, row.Destination, row.Ports) { ManagedBy = owner }, ct)));
                }
            }

            // Endpoints. Target keys are a tenant's (one integration's targets can't take another's), so two endpoints that would make the same
            // key (an email endpoint's ops-2, and an endpoint named ops-2) are both conflicts.
            var mappedEndpoints = legacy.Endpoints.Select(e => (Legacy: e, Mapped: EndpointMapping.Map(LegacyEndpoint.From(e), MappingOptions))).ToList();
            var keyOwners = mappedEndpoints.Where(m => m.Mapped.Succeeded)
                .SelectMany(m => m.Mapped.Value!.Targets.Select(t => (t.Key, Endpoint: m.Legacy.Key)).Append((m.Legacy.Key, Endpoint: m.Legacy.Key)))
                .Distinct().GroupBy(x => x.Key).Where(g => g.Count() > 1).SelectMany(g => g.Select(x => x.Endpoint)).ToHashSet(StringComparer.Ordinal);
            var migrating = new HashSet<string>(StringComparer.Ordinal);
            // Endpoints that migrate once notifications takes their digest (M46-1): a policy naming one waits with it.
            var waiting = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (endpoint, mapped) in mappedEndpoints)
            {
                var item = (Subject: MigrationSubject.Endpoint, endpoint.Key);
                if (!mapped.Succeeded)
                {
                    items.Add(new(item.Subject, item.Key, MigrationAction.Conflict, mapped.Problems));
                    continue;
                }
                if (keyOwners.Contains(endpoint.Key))
                {
                    items.Add(new(item.Subject, item.Key, MigrationAction.Conflict,
                        [new(ProblemCodes.KeyTaken, "Another legacy endpoint's key, or one of its email targets' keys (key-2, key-3…), is one of this endpoint's.")]));
                    continue;
                }
                var setup = mapped.Value!;
                if (setup.Route.DigestSeconds > 0 && !RouteDigestSupported)
                {
                    items.Add(new(item.Subject, item.Key, MigrationAction.PendingFeature, [new(ProblemCodes.PendingFeature,
                        $"The endpoint groups for {setup.Route.DigestSeconds} s: its route's digestSeconds comes with notifications M46-1. It migrates then, unchanged.")]));
                    waiting.Add(endpoint.Key);
                    continue;
                }
                var owner = OwnerOf(endpoint.ManagedBy);
                var (action, problems, surplus) = Compare(setup, current, owner);
                items.Add(new(item.Subject, item.Key, action, problems));
                if (action == MigrationAction.Conflict)
                {
                    continue;
                }
                migrating.Add(endpoint.Key);
                if (action == MigrationAction.Unchanged)
                {
                    continue;
                }
                // A new integration keeps the legacy endpoint's id (decision on Q-B): its id never changes, whichever pipeline answers.
                var keep = action == MigrationAction.Create ? endpoint.Id : (Guid?)null;
                writes.Add(new(item, (p, ct) => p.PutEndpointAsync(tenant, setup, surplus, keep, owner, ct)));
            }

            // Policies: their steps name the routes of endpoints that migrate.
            foreach (var policy in legacy.Policies)
            {
                var item = (Subject: MigrationSubject.Policy, policy.Key);
                var overlapping = legacy.Policies.Where(other => PolicyMapping.Overlap(policy, other)).Select(o => o.Key).ToList();
                // A step naming an endpoint that waits for M46-1 is no conflict: the policy waits too, and migrates as it is then.
                var mapped = PolicyMapping.Map(LegacyPolicy.From(policy), migrating.Union(waiting).ToHashSet(StringComparer.Ordinal));
                var problems = mapped.Problems.ToList();
                var waitsFor = policy.Steps.SelectMany(s => s.EndpointKeys).Where(waiting.Contains).Distinct().ToList();
                if (overlapping.Count > 0)
                {
                    problems.Add(new(ProblemCodes.OverlappingPolicies,
                        $"An alert can match this policy and {string.Join(", ", overlapping)}; notifications runs one escalation per alert. Narrow their rules or environments so none overlap."));
                }
                if (problems.Count > 0)
                {
                    items.Add(new(item.Subject, item.Key, MigrationAction.Conflict, problems));
                    continue;
                }
                var definition = mapped.Value!;
                current.Policies.TryGetValue(definition.Key, out var existing);
                if (existing is not null && !AliasVocabulary.Writes(AliasVocabulary.WebhooksAlias, existing.ManagedBy))
                {
                    items.Add(new(item.Subject, item.Key, MigrationAction.Conflict, [Taken("escalation policy", definition.Key, existing.ManagedBy)]));
                    continue;
                }
                // A row in the way is a conflict first: waiting would not clear it.
                if (!StepsNameRoutes)
                {
                    items.Add(new(item.Subject, item.Key, MigrationAction.PendingFeature, [new(ProblemCodes.PendingFeature,
                        "Its steps name endpoints' routes by key, which the notifications version this host runs can't take yet (M46-0); the policy migrates once it can, unchanged.")]));
                    continue;
                }
                if (waitsFor.Count > 0)
                {
                    items.Add(new(item.Subject, item.Key, MigrationAction.PendingFeature, [new(ProblemCodes.PendingFeature,
                        $"A step names {string.Join(", ", waitsFor)}, which migrates with notifications M46-1 (its group window); the policy migrates then, unchanged.")]));
                    continue;
                }
                var action = existing is null ? MigrationAction.Create
                    : EndpointMapping.Same(existing.Steps, definition.Steps) && existing.StopOnAcknowledge == definition.StopOnAcknowledge
                        ? MigrationAction.Unchanged : MigrationAction.Update;
                items.Add(new(item.Subject, item.Key, action, []));
                if (action != MigrationAction.Unchanged)
                {
                    writes.Add(new(item, (p, ct) => p.PutAsync(new PutEscalationPolicy(tenant, definition.Key, JsonElement.Parse(definition.Steps), definition.StopOnAcknowledge)
                        { ManagedBy = AliasVocabulary.WebhooksAlias }, ct)));
                }
            }
            return new PlanResult(items, writes);
        }

        /// <summary>
        /// How the rows there compare with an endpoint's setup: a conflict when a row in the way isn't the alias's; unchanged when every row is as
        /// mapped and the integration has no other target; else an update, with the alias's targets the setup no longer has (to delete).
        /// </summary>
        private static (MigrationAction, IReadOnlyList<MappingProblem>, IReadOnlyList<string>) Compare(EndpointSetup setup, Current current, string owner)
        {
            var problems = new List<MappingProblem>();
            current.Integrations.TryGetValue(setup.Integration.Key, out var integration);
            current.Routes.TryGetValue(setup.Route.Key, out var route);
            if (integration is not null && !AliasVocabulary.Writes(owner, integration.ManagedBy))
            {
                problems.Add(Taken("integration", integration.Key, integration.ManagedBy));
            }
            else if (integration is not null && integration.Channel != setup.Integration.Channel)
            {
                problems.Add(new(ProblemCodes.KeyTaken, $"The alias's integration '{integration.Key}' is a {integration.Channel} one, and an integration's channel is fixed: delete it in notifications first."));
            }
            if (route is not null && !AliasVocabulary.Writes(owner, route.ManagedBy))
            {
                problems.Add(Taken("route", route.Key, route.ManagedBy));
            }
            foreach (var target in setup.Targets)
            {
                if (current.Targets.TryGetValue(target.Key, out var existing)
                    && (!AliasVocabulary.Writes(owner, existing.ManagedBy) || current.IntegrationKeys.GetValueOrDefault(existing.IntegrationId) != setup.Integration.Key))
                {
                    problems.Add(!AliasVocabulary.Writes(owner, existing.ManagedBy) ? Taken("target", target.Key, existing.ManagedBy)
                        : new(ProblemCodes.KeyTaken, $"The target '{target.Key}' is the alias's, under the integration {current.IntegrationKeys.GetValueOrDefault(existing.IntegrationId)}."));
                }
            }
            if (problems.Count > 0)
            {
                return (MigrationAction.Conflict, problems, []);
            }
            var others = integration is null ? []
                : current.Targets.Values.Where(t => t.IntegrationId == integration.Id && !setup.Targets.Any(d => d.Key == t.Key)).Select(t => t.Key).Order().ToList();
            var foreign = others.Where(k => !AliasVocabulary.Writes(owner, current.Targets[k].ManagedBy)).ToList();
            if (foreign.Count > 0)
            {
                return (MigrationAction.Conflict, [new(ProblemCodes.KeyTaken, $"The integration '{integration!.Key}' has targets the alias doesn't own: {string.Join(", ", foreign)}.")], []);
            }
            if (integration is null && route is null && setup.Targets.All(t => !current.Targets.ContainsKey(t.Key)))
            {
                return (MigrationAction.Create, [], []);
            }
            // Written as another owner (the alias's configuration taking over the alias's row): an update, though nothing else changed.
            var same = integration is not null && route is not null && others.Count == 0
                && integration.ManagedBy == owner && route.ManagedBy == owner && setup.Targets.All(t => current.Targets[t.Key].ManagedBy == owner)
                && Equals(Definition(integration), setup.Integration with { Settings = "" }) && EndpointMapping.Same(integration.Settings, setup.Integration.Settings)
                && route.Targets.SequenceEqual(setup.Route.Targets) && route.Enabled == setup.Route.Enabled && route.DigestSeconds == setup.Route.DigestSeconds && EndpointMapping.Same(route.Match, setup.Route.Match)
                && setup.Targets.All(t => current.Targets[t.Key] is var x && (x.Address, x.AddressRef, x.Enabled) == (t.Address, t.AddressRef, t.Enabled));
            return (same ? MigrationAction.Unchanged : MigrationAction.Update, [], others);
        }

        /// <summary>An integration row as a definition, its settings left out (compared as JSON).</summary>
        private static IntegrationDefinition Definition(Integration i) => new(i.Key, i.Channel, i.Kind, "", i.SecretRef, i.Priority, i.Enabled);

        /// <summary>The setup the alias's rows for one endpoint key make, or null when they aren't all there: what the alias (M46b) reads back.</summary>
        public static async Task<EndpointSetup?> ReadSetupAsync(NotificationsDbContext db, Guid tenantId, string key, CancellationToken ct)
        {
            var integration = await db.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Key == key && (i.ManagedBy == AliasVocabulary.WebhooksAlias || i.ManagedBy == AliasVocabulary.AliasConfig), ct);
            var route = await db.Routes.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Key == key && (r.ManagedBy == AliasVocabulary.WebhooksAlias || r.ManagedBy == AliasVocabulary.AliasConfig), ct);
            if (integration is null || route is null)
            {
                return null;
            }
            var targets = await db.IntegrationTargets.AsNoTracking().Where(t => t.TenantId == tenantId && t.IntegrationId == integration.Id).ToListAsync(ct);
            var ordered = route.Targets.Select(k => targets.SingleOrDefault(t => t.Key == k)).ToList();
            if (ordered.Any(t => t is null) || targets.Count != ordered.Count)
            {
                return null;
            }
            return new EndpointSetup(Definition(integration) with { Settings = integration.Settings },
                ordered.Select(t => new TargetDefinition(key, t!.Key, t.Address, t.AddressRef, t.Enabled)).ToList(),
                new RouteDefinition(route.Key, route.Match, route.Targets, route.Enabled, route.DigestSeconds));
        }

        // ─── The writes ───────────────────────────────────────────────────────

        private Task<MappingProblem?> PutEndpointAsync(Guid tenant, EndpointSetup setup, IReadOnlyList<string> surplus, Guid? id, string owner, CancellationToken ct)
            => EndpointWrites.PutAsync(new Sender(this), tenant, setup, surplus, id, ct, owner);

        private async Task<MappingProblem?> PutAsync<T>(IRequest<Outcome<T>> command, CancellationToken ct)
            => EndpointWrites.Problem(await mediator.Send(command, ct));

        /// <summary>The migration's commands, each saved in its transaction before the next.</summary>
        private sealed class Sender(MigrationPlanner planner) : ISetupSender
        {
            public async Task<MappingProblem?> SendAsync<T>(IRequest<Outcome<T>> command, CancellationToken ct)
            {
                var problem = await planner.PutAsync(command, ct);
                if (problem is null)
                {
                    await planner.Db.SaveChangesAsync(ct);
                }
                return problem;
            }
        }
    }
}
