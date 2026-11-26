using System.Text.Json;
using Juice.Notifications.Embedded;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Notifications.Migration;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api
{
    /// <summary>
    /// <c>notifications migrate [--apply] [--tenant &lt;id&gt;]</c> (phase-5-plan I70, M46b): the migration tool from the host's own command line,
    /// as the endpoint runs it. It prints the report as JSON and exits: 0 when nothing is in conflict, 2 when something is, 1 when it couldn't
    /// run. The tenant (its row's Id, as the API routes name it) is the platform's unless named, and only the platform's is taken. The host's
    /// hosted services don't start.
    /// </summary>
    public sealed record NotificationsMigrationCommand(bool Apply, Guid? TenantId)
    {
        /// <summary>The command the arguments name, or null when they name none (the host runs as a server).</summary>
        public static NotificationsMigrationCommand? Parse(string[] args)
        {
            if (args is not ["notifications", "migrate", .. var rest])
            {
                return null;
            }
            var (apply, tenant) = (false, (Guid?)null);
            for (var i = 0; i < rest.Length; i++)
            {
                switch (rest[i])
                {
                    case "--apply":
                        apply = true;
                        break;
                    case "--tenant" when i + 1 < rest.Length && Guid.TryParse(rest[i + 1], out var id):
                        tenant = id;
                        i++;
                        break;
                    default:
                        throw new ArgumentException($"notifications migrate [--apply] [--tenant <id>]: '{rest[i]}' isn't an option it takes.");
                }
            }
            return new NotificationsMigrationCommand(apply, tenant);
        }

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        public async Task<int> RunAsync(IServiceProvider services, TextWriter output, CancellationToken ct)
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // The platform tenant's row unless one is named: the one whose key is the embedded tenant's Id.
            var key = scope.ServiceProvider.GetRequiredService<NotificationsEmbeddedOptions>().TenantId.ToString("D");
            var tenant = TenantId ?? await db.Tenants.AsNoTracking().Where(t => t.Key == key).Select(t => t.Id).SingleOrDefaultAsync(ct);
            var planner = scope.ServiceProvider.GetRequiredService<MigrationPlanner>();
            MigrationReport report;
            try
            {
                var legacy = await LegacySetup.ReadAsync(db, tenant, ct);
                report = Apply ? await planner.ApplyAsync(legacy, ct) : await planner.ReportAsync(legacy, ct);
            }
            catch (ArgumentException ex)
            {
                await output.WriteLineAsync(ex.Message);
                return 1;
            }
            if (Apply)
            {
                db.AuditRecords.Add(new AuditRecord
                {
                    TenantId = tenant, Actor = MigrationPlanner.Source, Operation = "notifications.migrate.apply", TargetType = "notifications_migration",
                    TargetId = tenant.ToString(), Outcome = report.Applied ? AuditOutcome.Succeeded : AuditOutcome.Failed,
                    Metadata = NotificationsMigrationEndpoints.Counts(report),
                });
                await db.SaveChangesAsync(ct);
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(NotificationsMigrationReportDto.From(report), Json));
            return report.HasConflicts || Apply && !report.Applied ? 2 : 0;
        }
    }
}
