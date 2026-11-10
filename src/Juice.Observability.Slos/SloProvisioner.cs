using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Slos
{
    /// <summary>
    /// Loads <c>Slos:Definitions</c> at startup (roadmap Phase 9): an invalid file stops the host. A new key is created at version 1; a changed
    /// definition becomes the next version from its <c>EffectiveFrom</c> (now by default, never in the past), withdrawing any pending version it
    /// takes effect before; a key the file no longer lists is
    /// retired, and comes back if it's listed again. Nothing is edited in place. Each change is audited and recorded on the timeline, in one
    /// transaction, one replica at a time.
    /// </summary>
    public sealed class SloProvisioner(IServiceScopeFactory scopes, IOptions<SloOptions> options, IOptions<PlatformOptions> platform, TimeProvider clock,
        ILogger<SloProvisioner> logger) : IHostedService
    {
        public const string Actor = "deployment-config";

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var errors = options.Value.Validate().ToList();
            if (errors.Count > 0) throw new InvalidOperationException("Invalid SLO configuration:\n" + string.Join("\n", errors));
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({WorkerLease.KeyOf("slo-provisioning")})", cancellationToken);
            var warnings = new List<string>();
            var changes = await ProvisionAsync(db, options.Value.Definitions, platform.Value.TenantKey, clock.GetUtcNow(), cancellationToken, warnings);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            foreach (var warning in warnings) logger.LogError("SLO burn alerts: {Problem}", warning);
            if (!options.Value.Enabled)
            {
                // Nothing counts, so nothing would ever judge the burn alerts again: close the open ones rather than let them escalate forever.
                using var context = PlatformMessageContext.Begin("slo-provisioning");
                var closed = await scope.ServiceProvider.GetRequiredService<Juice.MediatR.IMediator>()
                    .Send(new CloseSloBurnAlerts(CloseSloBurnAlertsHandler.CountingDisabled), cancellationToken);
                if (closed > 0) logger.LogWarning("SLO counting is off: {Count} open burn alerts resolved", closed);
            }
            logger.LogInformation("SLOs loaded: {Count} configured, {Changes} changed", options.Value.Definitions.Count, changes);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// Makes the SLOs match the file; returns how many changed. Only adds and changes entities: the caller saves them. <paramref name="warnings"/>
        /// collects what's wrong but doesn't stop starting (a burn rule key another rule holds).
        /// </summary>
        public static async Task<int> ProvisionAsync(PlatformDbContext db, IReadOnlyList<SloDefinition> definitions, string tenantKey, DateTimeOffset now, CancellationToken ct,
            ICollection<string>? warnings = null)
        {
            var tenantId = await db.Tenants.Where(t => t.Key == tenantKey).Select(t => t.Id).SingleAsync(ct);
            var existing = await db.Slos.Where(s => s.TenantId == tenantId).ToDictionaryAsync(s => s.Key, ct);
            var versionsOf = (await db.SloVersions.Where(v => v.TenantId == tenantId).ToListAsync(ct))
                .GroupBy(v => v.SloId).ToDictionary(g => g.Key, g => (IReadOnlyList<SloVersion>)[.. g.OrderBy(v => v.Version)]);
            var latest = versionsOf.ToDictionary(p => p.Key, p => p.Value[^1]);
            var changes = 0;
            void Record(string operation, Slo slo, Dictionary<string, string> metadata, string summary)
            {
                db.AuditRecords.Add(new AuditRecord
                {
                    TenantId = tenantId, Actor = Actor, Operation = operation, TargetType = "slo", TargetId = slo.Key, Outcome = AuditOutcome.Succeeded, Metadata = metadata,
                });
                db.ChangeEvents.Add(new ChangeEvent
                {
                    TenantId = tenantId, Kind = ChangeKind.Configuration, Source = ChangeEvent.PlatformSource, Actor = Actor, Summary = summary,
                    Attributes = new() { ["slo"] = slo.Key }, OccurredAt = now, RecordedAt = now,
                });
                changes++;
            }
            SloVersion Version(Slo slo, SloDefinition definition, int number, DateTimeOffset effective)
            {
                var version = new SloVersion
                {
                    TenantId = tenantId, SloId = slo.Id, Version = number, Definition = definition.ToJson(), Hash = definition.Hash(), Target = definition.Target,
                    EffectiveFrom = effective, CreatedAt = now, CreatedBy = Actor,
                };
                db.SloVersions.Add(version);
                return version;
            }

            var problems = new List<string>();
            foreach (var definition in definitions)
            {
                // Stored in UTC: PostgreSQL takes no other offset, and a date written as +07:00 is the same moment.
                var effective = (definition.EffectiveFrom ?? now).ToUniversalTime();
                if (!existing.TryGetValue(definition.Key, out var slo))
                {
                    slo = new Slo
                    {
                        TenantId = tenantId, Key = definition.Key, Name = definition.Name, Category = definition.Category, Sli = definition.Sli,
                        Service = definition.Service, Environment = definition.Environment, CurrentVersion = 1, CreatedAt = now,
                    };
                    db.Slos.Add(slo);
                    Version(slo, definition, 1, effective);
                    Record("slo.create", slo, Metadata(definition, 1, effective), $"SLO {slo.Key}: created, {definition.Target}% {definition.Sli}");
                    continue;
                }
                if (slo.Sli != definition.Sli)
                {
                    problems.Add($"slo '{slo.Key}': its Sli is {slo.Sli}; measuring something else is a new SLO with a new Key");
                    continue;
                }
                var current = latest[slo.Id];
                // A changed definition is a new version. So is a new date for one still waiting for its date: moving it earlier or later.
                var pending = current.EffectiveFrom > now;
                var redated = pending && (current.EffectiveFrom - effective).Duration() > TimeSpan.FromSeconds(1);
                if (current.Hash != definition.Hash() || redated)
                {
                    if (effective < now - TimeSpan.FromMinutes(1))
                    {
                        problems.Add($"slo '{slo.Key}': a new version can't take effect in the past ({effective:u}): what was counted stays counted");
                        continue;
                    }
                    // Every version still waiting for its date is withdrawn: the file says what applies next, and it never will.
                    var withdrawn = versionsOf[slo.Id].Where(v => v.EffectiveFrom > now && !SloCounter.Withdrawn(v)).ToList();
                    foreach (var v in withdrawn) v.WithdrawnAt = now;
                    slo.CurrentVersion = current.Version + 1;
                    // The row shows the newest version as loaded; counting and the API go by the version in effect at each minute.
                    slo.Name = definition.Name;
                    slo.Service = definition.Service;
                    slo.Environment = definition.Environment;
                    Version(slo, definition, slo.CurrentVersion, effective);
                    var metadata = Metadata(definition, slo.CurrentVersion, effective);
                    var numbers = string.Join(", ", withdrawn.Select(v => v.Version));
                    if (withdrawn.Count > 0) metadata["withdraws"] = numbers.Replace(" ", "", StringComparison.Ordinal);
                    Record("slo.version", slo, metadata, $"SLO {slo.Key}: version {slo.CurrentVersion} from {effective:u}, {definition.Target}%"
                        + (withdrawn.Count > 0 ? $", withdrawing pending version {numbers}" : ""));
                }
                if (slo.Retired)
                {
                    slo.Retired = false;
                    slo.RetiredAt = null;
                    Record("slo.restore", slo, new() { ["version"] = slo.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) }, $"SLO {slo.Key}: listed again");
                }
            }
            // The rules their burn alerts belong to, once there are SLOs to have them.
            if (definitions.Count > 0 || existing.Count > 0) await SloBurnAlerts.EnsureRulesAsync(db, tenantId, now, warnings ?? [], ct);
            if (problems.Count > 0) throw new InvalidOperationException("Invalid SLO configuration:\n" + string.Join("\n", problems));
            var listed = definitions.Select(d => d.Key).ToHashSet();
            foreach (var removed in existing.Values.Where(s => !s.Retired && !listed.Contains(s.Key)))
            {
                removed.Retired = true;
                removed.RetiredAt = now;
                Record("slo.retire", removed, [], $"SLO {removed.Key}: retired (no longer in the file)");
            }
            return changes;
        }

        private static Dictionary<string, string> Metadata(SloDefinition definition, int version, DateTimeOffset effective) => new()
        {
            ["version"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["sli"] = definition.Sli,
            ["target"] = definition.Target.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["effectiveFrom"] = effective.ToString("O"),
            ["hash"] = definition.Hash(),
        };
    }

    public static class SloServiceCollectionExtensions
    {
        /// <summary>SLO provisioning (always: the API reads what it loads) and, when <c>Slos:Enabled</c>, the counter. Register after the tenant provisioner.</summary>
        public static IServiceCollection AddPlatformSlos(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<SloOptions>(configuration.GetSection(SloOptions.Section));
            services.AddHostedService<SloProvisioner>();
            if (configuration.GetSection(SloOptions.Section).GetValue(nameof(SloOptions.Enabled), true))
            {
                services.AddSingleton<SloCounter>();
                services.AddHostedService(sp => sp.GetRequiredService<SloCounter>());
            }
            return services;
        }
    }
}
