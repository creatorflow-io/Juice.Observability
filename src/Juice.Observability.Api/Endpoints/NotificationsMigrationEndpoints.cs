using Juice.Observability.Data;
using Juice.Observability.Notifications.Migration;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary><c>apply: false</c> (the default) reports only; <c>true</c> writes what isn't a conflict, in one transaction.</summary>
    public sealed record NotificationsMigrateRequest(bool? Apply);

    public sealed record MigrationProblemDto(string Code, string Message);

    public sealed record MigrationItemDto(string Subject, string Key, string Action, IReadOnlyList<MigrationProblemDto> Problems);

    /// <summary>The report: per legacy endpoint, policy and allowlist entry, what the migration does with it, and whether it was applied.</summary>
    public sealed record NotificationsMigrationReportDto(Guid TenantId, bool Applied, bool HasConflicts, IReadOnlyList<MigrationItemDto> Items)
    {
        public static NotificationsMigrationReportDto From(MigrationReport report) => new(report.TenantId, report.Applied, report.HasConflicts,
            [.. report.Items.Select(i => new MigrationItemDto(Name(i.Subject.ToString()), i.Key, Name(i.Action.ToString()),
                [.. i.Problems.Select(p => new MigrationProblemDto(p.Code, p.Message))]))]);

        /// <summary>An enum's name as the API writes its words: <c>PendingFeature</c> is <c>pending_feature</c>.</summary>
        internal static string Name(string value) => string.Concat(value.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));
    }

    /// <summary>
    /// The migration tool (phase-5-plan I70, M46b): the legacy webhook setup of the platform tenant mapped onto the embedded notifications
    /// pipeline. A report first, then the same with <c>apply</c>. Admin only; an apply is audited with its counts. The legacy rows aren't
    /// changed.
    /// </summary>
    public static class NotificationsMigrationEndpoints
    {
        public static RouteGroupBuilder MapNotificationsMigration(this RouteGroupBuilder tenant)
        {
            tenant.MapPost("/notifications/migrate", MigrateAsync);
            return tenant;
        }

        private static async Task<IResult> MigrateAsync(Guid tenantId, NotificationsMigrateRequest? request, HttpContext http, PlatformDbContext db,
            IServiceScopeFactory scopes, CancellationToken ct)
        {
            var apply = request?.Apply == true;
            var mutation = new Mutation(http, db, tenantId, apply ? "notifications.migrate.apply" : "notifications.migrate.report", "notifications_migration");
            if (await mutation.RequireAsync(tenantId.ToString(), ct, Roles.Admin) is { } denied)
            {
                return denied;
            }
            var legacy = await LegacySetup.ReadAsync(db, tenantId, ct);
            MigrationReport report;
            // The pipeline's own scope and transaction, never the platform's (waves §5.3).
            await using (var scope = scopes.CreateAsyncScope())
            {
                var planner = scope.ServiceProvider.GetRequiredService<MigrationPlanner>();
                ReconcileWhileLegacySends(planner, scope.ServiceProvider);
                try
                {
                    report = apply ? await planner.ApplyAsync(legacy, ct, http.User.Actor()) : await planner.ReportAsync(legacy, ct);
                }
                catch (ArgumentException ex)
                {
                    return Mutation.Problem(StatusCodes.Status400BadRequest, "Not the platform tenant", ex.Message);
                }
            }
            if (apply)
            {
                mutation.Audit(report.Applied ? AuditOutcome.Succeeded : AuditOutcome.Failed, tenantId.ToString(), Counts(report));
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(NotificationsMigrationReportDto.From(report));
        }

        /// <summary>
        /// While legacy sends, its tables are the whole of what the alias's own rows mirror: the migration also removes and disables what they no
        /// longer have (M46c drift). In new there is nothing of legacy's to go by, so nothing is reconciled.
        /// </summary>
        internal static void ReconcileWhileLegacySends(MigrationPlanner planner, IServiceProvider services)
        {
            if (services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlatformNotificationsOptions>>().Value.LegacySends)
            {
                planner.Reconciles = new HashSet<string> { AliasVocabulary.WebhooksAlias };
            }
        }

        internal static Dictionary<string, string> Counts(MigrationReport report)
            => report.Items.GroupBy(i => NotificationsMigrationReportDto.Name(i.Action.ToString()))
                .ToDictionary(g => g.Key, g => g.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
