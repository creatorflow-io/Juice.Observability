using Juice.Observability.Data;
using Juice.Observability.Signals;
using Microsoft.EntityFrameworkCore;
using static Juice.Observability.Discovery.ClickHouseTelemetrySource;

namespace Juice.Observability.Rules
{
    /// <summary>
    /// Whether a rule of a signal pack's template can ever observe the service it names. A pack's template reads only what the pack's services send,
    /// so one scoped to a service that has never sent it would be quiet for good, never firing and never saying why. A service discovery hasn't
    /// seen yet isn't judged: the rule may come before it.
    /// </summary>
    public static class SignalCoverage
    {
        /// <summary>Why the rule would never observe its service, or null when it can (or can't be told yet).</summary>
        public static async Task<string?> QuietReasonAsync(PlatformDbContext db, Guid tenantId, RuleDefinition rule, CancellationToken cancellationToken)
            => (await QuietReasonsAsync(db, tenantId, [rule], cancellationToken)).GetValueOrDefault(rule.Key);

        /// <summary><see cref="QuietReasonAsync"/> for many rules at once, by rule key; rules that can observe their service are left out.</summary>
        public static async Task<IReadOnlyDictionary<string, string>> QuietReasonsAsync(PlatformDbContext db, Guid tenantId,
            IReadOnlyCollection<RuleDefinition> rules, CancellationToken cancellationToken)
        {
            var scoped = rules.Where(r => r.Service.Length > 0 && SignalPacks.PackOf(r.Template) is not null).ToList();
            if (scoped.Count == 0) return new Dictionary<string, string>();
            var names = scoped.Select(r => SplitService(r.Service).Name).Distinct().ToList();
            var services = await db.Services.AsNoTracking().Where(s => s.TenantId == tenantId && names.Contains(s.Name))
                .Select(s => new { s.Namespace, s.Name, s.SignalPacks }).ToListAsync(cancellationToken);
            var reasons = new Dictionary<string, string>();
            foreach (var rule in scoped)
            {
                var (ns, name) = SplitService(rule.Service);
                var pack = SignalPacks.PackOf(rule.Template)!;
                var known = services.Where(s => s.Namespace == ns && s.Name == name).ToList();
                if (known.Count > 0 && !known.Any(s => s.SignalPacks.Contains(pack)))
                {
                    reasons[rule.Key] = $"{rule.Service} has sent no {pack} signals, so a {rule.Template} rule would never observe it";
                }
            }
            return reasons;
        }
    }
}
