using System.Globalization;
using Juice.Domain;
using Juice.MediatR;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Slos
{
    /// <summary>One of the platform's burn alerts: the rule it belongs to, and the rate both of its windows must reach.</summary>
    public sealed record BurnAlert(string RuleKey, string Name, AlertSeverity Severity, double Rate, string Long, string Short);

    /// <summary>
    /// <see cref="Burning"/>: both windows at or over the rate, so it fires. <see cref="Cooled"/>: the short window under it, so it resolves.
    /// <see cref="Between"/>: the short window burning but not yet the long one, so nothing changes. <see cref="Unknown"/>: either window has no rate
    /// (too many minutes unread, or too few requests), so nothing changes either: missing data neither fires nor resolves.
    /// </summary>
    public enum BurnCondition { Burning, Between, Cooled, Unknown }

    /// <summary>
    /// Multiwindow burn-rate alerts (Phase 9 plan, milestone 2). Each SLO can have one open alert per <see cref="All"/> entry. They are ordinary alerts of
    /// the platform's three <see cref="SloBurnRules"/>: their subject is the SLO's service in its environment, so routing, silences, escalation,
    /// webhooks and incidents treat them as any other alert.
    /// </summary>
    public static class SloBurnAlerts
    {
        public static readonly IReadOnlyList<BurnAlert> All =
        [
            new(SloBurnRules.FastBurn, "SLO fast burn: 14.4× over 1 h and 5 min", AlertSeverity.Critical, 14.4, "1h", "5m"),
            new(SloBurnRules.SlowBurn, "SLO slow burn: 6× over 6 h and 30 min", AlertSeverity.Warning, 6, "6h", "30m"),
            new(SloBurnRules.SlowBurn3d, "SLO slow burn: 1× over 3 days and 6 h", AlertSeverity.Warning, 1, "3d", "6h"),
        ];

        /// <summary>
        /// Fires on both windows (the long one shows it's sustained, the short one that it's still happening) and resolves on the short one: once it's
        /// under the rate the burn has stopped, though the long window remembers it for up to its length.
        /// </summary>
        public static BurnCondition Classify(BurnAlert alert, WindowStatus longWindow, WindowStatus shortWindow)
            => longWindow.BurnRate is not { } l || shortWindow.BurnRate is not { } s ? BurnCondition.Unknown
                : s < alert.Rate ? BurnCondition.Cooled
                : l >= alert.Rate ? BurnCondition.Burning
                : BurnCondition.Between;

        /// <summary>What the rules API shows for a burn rule. Nothing schedules it: the SLO counter evaluates it after each pass.</summary>
        public static RuleDefinition Definition(BurnAlert alert) => new()
        {
            Key = alert.RuleKey, Name = alert.Name, Template = RuleTemplate.SloBurn, Severity = alert.Severity, Threshold = alert.Rate,
            RecoveryThreshold = alert.Rate, Window = SloBudget.BurnWindows.Single(w => w.Name == alert.Long).Length, Schedule = TimeSpan.FromSeconds(30),
            ForDuration = TimeSpan.Zero, RecoveryDuration = TimeSpan.Zero, Cooldown = Cooldown(alert), MinimumSamples = 1,
        };

        /// <summary>
        /// After a burn alert resolves, a new one for the same SLO waits this long: its short window, and at least 10 minutes as a rule's default.
        /// Without it, a burn hovering at the rate would fire and resolve on every pass, the long window still remembering it.
        /// </summary>
        public static TimeSpan Cooldown(BurnAlert alert)
        {
            var shortWindow = SloBudget.BurnWindows.Single(w => w.Name == alert.Short).Length;
            return shortWindow > TimeSpan.FromMinutes(10) ? shortWindow : TimeSpan.FromMinutes(10);
        }

        /// <summary>
        /// The versions counted for the same service and environment as <paramref name="current"/>: burn windows judge only those, since minutes
        /// counted for another service before a move say nothing about this one. <paramref name="versions"/> are the SLO's. An older version this build
        /// can't read is left out, its minutes looking unread, rather than holding every alert of the SLO.
        /// </summary>
        public static IReadOnlySet<int> SameServiceVersions(IEnumerable<SloVersion> versions, SloDefinition current)
            => versions.Select(v => (v.Version, Definition: TryRead(v.Definition)))
                .Where(v => v.Definition is { } d && d.Service == current.Service && d.Environment == current.Environment).Select(v => v.Version).ToHashSet();

        private static SloDefinition? TryRead(string json)
        {
            try
            {
                return SloDefinition.FromJson(json);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public const string Actor = "platform";

        /// <summary>
        /// Creates the three burn rules, or brings them up to date. A rule of another owner holding one of their keys (made before the key was
        /// reserved) is never taken over: that kind of burn alert is left out and <paramref name="conflicts"/> says why, but nothing stops starting,
        /// since rules can't be deleted or renamed. Only adds and changes entities: the caller saves them.
        /// </summary>
        public static async Task EnsureRulesAsync(PlatformDbContext db, Guid tenantId, DateTimeOffset now, ICollection<string> conflicts, CancellationToken ct)
        {
            foreach (var burn in All)
            {
                var definition = Definition(burn);
                var hash = definition.Hash();
                var rule = await db.Rules.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Key == burn.RuleKey, ct);
                string operation;
                if (rule is null)
                {
                    rule = new Rule
                    {
                        TenantId = tenantId, Key = burn.RuleKey, Name = burn.Name, Template = RuleTemplate.SloBurn, Severity = burn.Severity,
                        Definition = definition.ToJson(), DefinitionHash = hash, Enabled = true, LoadedAt = now, ManagedBy = ManagedBy.Platform,
                    };
                    db.Rules.Add(rule);
                    operation = "rule.create";
                }
                else if (rule.ManagedBy != ManagedBy.Platform)
                {
                    conflicts.Add($"rule '{burn.RuleKey}' ({rule.ManagedBy}) holds a key reserved for SLO burn alerts, so SLOs raise no '{burn.RuleKey}' alerts");
                    continue;
                }
                else if (rule.DefinitionHash != hash || rule.Template != RuleTemplate.SloBurn || !rule.Enabled)
                {
                    // Changed here, or set aside for a rollback to a build without burn alerts (see deployment.md): put back as the platform's.
                    rule.Template = RuleTemplate.SloBurn;
                    rule.Enabled = true;
                    rule.Name = burn.Name;
                    rule.Severity = burn.Severity;
                    rule.Definition = definition.ToJson();
                    rule.DefinitionHash = hash;
                    rule.LoadedAt = now;
                    operation = "rule.update";
                }
                else
                {
                    continue;
                }
                db.AuditRecords.Add(new AuditRecord
                {
                    TenantId = tenantId, Actor = Actor, Operation = operation, TargetType = "rule", TargetId = rule.Key, Outcome = AuditOutcome.Succeeded,
                    Metadata = new() { ["hash"] = hash, ["template"] = nameof(RuleTemplate.SloBurn) },
                });
                db.ChangeEvents.Add(new ChangeEvent
                {
                    TenantId = tenantId, Kind = ChangeKind.Configuration, Source = ChangeEvent.PlatformSource, Actor = Actor,
                    Summary = $"Rule {rule.Key}: {operation.Replace("rule.", "", StringComparison.Ordinal)}", Attributes = new() { [ChangeEvent.RuleAttribute] = rule.Key },
                    OccurredAt = now, RecordedAt = now,
                });
            }
        }
    }

    /// <summary>
    /// Resolve every open burn alert, for <paramref name="Reason"/>: counting is off (<c>Slos:Enabled</c> false), so nothing would ever judge them
    /// again, and a firing alert left alone would escalate forever.
    /// </summary>
    public sealed record CloseSloBurnAlerts(string Reason) : MessageBase, IRequest<int>;

    public sealed class CloseSloBurnAlertsHandler(PlatformDbContext db, IOptions<PlatformOptions> platform, TimeProvider clock) : IRequestHandler<CloseSloBurnAlerts, int>
    {
        public const string CountingDisabled = "slo_counting_disabled";

        /// <summary>The counter's lease (<see cref="SloCounter"/>): while a replica holds it, burn alerts are still judged there.</summary>
        public const string CounterLease = "slos";

        public async ValueTask<int> Handle(CloseSloBurnAlerts request, CancellationToken cancellationToken)
        {
            // Another replica counting (this one has Slos:Enabled false, as a web-only replica may): its alerts are live. Leave them.
            if (await WorkerLease.IsHeldAsync(db, CounterLease, cancellationToken))
            {
                return 0;
            }
            var tenantId = await db.Tenants.Where(t => t.Key == platform.Value.TenantKey).Select(t => t.Id).SingleAsync(cancellationToken);
            // By owner, not template: rules set aside for a rollback (see deployment.md) still own their alerts.
            var ruleIds = await db.Rules.Where(r => r.TenantId == tenantId && r.ManagedBy == ManagedBy.Platform).Select(r => r.Id).ToListAsync(cancellationToken);
            var open = await db.Alerts.Where(a => a.TenantId == tenantId && ruleIds.Contains(a.RuleId) && a.State != AlertState.Resolved).ToListAsync(cancellationToken);
            var now = clock.GetUtcNow();
            foreach (var alert in open)
            {
                AlertChanges.Resolve(db, alert, request.Reason, now, RuleTemplate.SloBurn);
            }
            return open.Count;
        }
    }

    /// <summary>Evaluate every SLO's burn alerts now. Their changes, and the notifications they raise, commit together through the TransactionBehavior.</summary>
    public sealed record EvaluateSloBurns : MessageBase, IRequest<SloBurnEvaluation>;

    public sealed record SloBurnEvaluation(int Slos, int Fired, int Resolved);

    /// <summary>
    /// Reads every active SLO's burn windows and applies <see cref="SloBurnAlerts.Classify"/> to each burn alert: fires when both windows burn at the rate,
    /// resolves when the short one is under it, and holds otherwise, missing data included. An SLO that was retired, or moved to another service, resolves its
    /// open alerts with that reason. Only changes entities: nothing here saves, begins or commits, or knows who is notified.
    /// </summary>
    public sealed class EvaluateSloBurnsHandler(PlatformDbContext db, IOptions<SloOptions> options, IOptions<PlatformOptions> platform, TimeProvider clock)
        : IRequestHandler<EvaluateSloBurns, SloBurnEvaluation>
    {
        public const string Fired = "burn_rate_over";
        public const string Cooled = "burn_rate_under";
        public const string SloRetired = "slo_retired";
        public const string SloMoved = "slo_moved";

        public async ValueTask<SloBurnEvaluation> Handle(EvaluateSloBurns request, CancellationToken cancellationToken)
        {
            var ct = cancellationToken;
            var tenantId = await db.Tenants.Where(t => t.Key == platform.Value.TenantKey).Select(t => t.Id).SingleAsync(ct);
            var rules = await db.Rules.AsNoTracking().Where(r => r.TenantId == tenantId && r.Template == RuleTemplate.SloBurn && r.ManagedBy == ManagedBy.Platform)
                .ToDictionaryAsync(r => r.Key, ct);
            if (rules.Count == 0)
            {
                return new(0, 0, 0);
            }
            var now = clock.GetUtcNow();
            // Alerts are dated by the end of the windows they were judged on.
            var at = SloCounter.Floor(now) - SloReader.Settle;
            var ruleIds = rules.Values.Select(r => r.Id).ToList();
            var open = await db.Alerts.Where(a => a.TenantId == tenantId && ruleIds.Contains(a.RuleId) && a.State != AlertState.Resolved).ToDictionaryAsync(a => a.DedupKey, ct);
            var slos = await db.Slos.AsNoTracking().Where(s => s.TenantId == tenantId && !s.Retired).ToListAsync(ct);
            var ids = slos.Select(s => s.Id).ToList();
            var versions = (await db.SloVersions.AsNoTracking().Where(v => ids.Contains(v.SloId)).OrderBy(v => v.Version).ToListAsync(ct)).ToLookup(v => v.SloId);
            // Each SLO's version in effect and its definition, read once; null for one this build can't read.
            var current = new Dictionary<Guid, Current?>();
            foreach (var slo in slos)
            {
                try
                {
                    var list = versions[slo.Id].ToList();
                    var version = SloCounter.VersionAt(list, now);
                    var definition = SloDefinition.FromJson(version.Definition);
                    current[slo.Id] = new(version, definition, SloBurnAlerts.SameServiceVersions(list, definition));
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    current[slo.Id] = null;
                }
            }
            // Minutes counted for another service (before a move) say nothing about this one: they're left out, and look unread, so the windows
            // read Unknown, and hold, until the new service's minutes fill them.
            var statuses = await SloReader.StatusesAsync(db, ids, id => [.. versions[id]], options.Value, now, ct, burnOnly: true,
                countedVersions: id => current.GetValueOrDefault(id)?.SameService);
            // Only the services and environments the SLOs name.
            var named = current.Values.OfType<Current>().Select(c => c.Definition).ToList();
            var serviceNames = named.Select(d => d.SplitService().Name).Distinct().ToList();
            var environmentKeys = named.Select(d => d.Environment).Distinct().ToList();
            var services = await db.Services.AsNoTracking().Where(s => s.TenantId == tenantId && serviceNames.Contains(s.Name))
                .Select(s => new { s.Id, s.Namespace, s.Name }).ToListAsync(ct);
            var environments = await db.Environments.AsNoTracking().Where(e => e.TenantId == tenantId && environmentKeys.Contains(e.Key))
                .ToDictionaryAsync(e => e.Key, e => e.Id, ct);
            // When each burn alert last cooled after firing: a new one waits out its cooldown, so a burn hovering at the rate doesn't flap. Only
            // a cooled burn counts: an alert closed for another reason (moved, retired, counting off) says nothing about the burn.
            var dedupKeys = slos.SelectMany(s => SloBurnAlerts.All.Select(b => SloBurnRules.DedupKey(b.RuleKey, s.Key))).ToList();
            var lastResolved = await db.AlertTransitions.AsNoTracking().Where(t => t.TenantId == tenantId && t.ToState == AlertState.Resolved && t.Reason == Cooled)
                .Join(db.Alerts.AsNoTracking().Where(a => dedupKeys.Contains(a.DedupKey) && a.FiredAt != null), t => t.AlertId, a => a.Id, (t, a) => new { a.DedupKey, t.At })
                .GroupBy(x => x.DedupKey).Select(g => new { g.Key, ResolvedAt = g.Max(x => x.At) }).ToDictionaryAsync(x => x.Key, x => (DateTimeOffset?)x.ResolvedAt, ct);

            int fired = 0, resolved = 0;
            var watched = new HashSet<string>();
            foreach (var slo in slos)
            {
                if (current[slo.Id] is not { } c)
                {
                    // An SLO this build can't read: its alerts stay as they are, neither fired nor resolved, like missing data.
                    foreach (var burn in SloBurnAlerts.All) watched.Add(SloBurnRules.DedupKey(burn.RuleKey, slo.Key));
                    continue;
                }
                var (version, definition) = (c.Version, c.Definition);
                var status = statuses[slo.Id];
                var (ns, name) = definition.SplitService();
                var subjectKey = RuleTemplates.ServiceSubjectKey(ns, name, definition.Environment);
                var serviceId = services.FirstOrDefault(s => s.Namespace == ns && s.Name == name)?.Id;
                Guid? environmentId = environments.TryGetValue(definition.Environment, out var e) ? e : null;
                foreach (var burn in SloBurnAlerts.All)
                {
                    if (!rules.TryGetValue(burn.RuleKey, out var rule))
                    {
                        continue;
                    }
                    var dedup = SloBurnRules.DedupKey(rule.Key, slo.Key);
                    watched.Add(dedup);
                    var longWindow = status.Windows.Single(w => w.Name == burn.Long);
                    var shortWindow = status.Windows.Single(w => w.Name == burn.Short);
                    var condition = SloBurnAlerts.Classify(burn, longWindow, shortWindow);
                    open.TryGetValue(dedup, out var alert);
                    // Its service changed with a new version: the alert was about the old one. Resolve it, and judge the new one afresh.
                    if (alert is not null && alert.SubjectKey != subjectKey)
                    {
                        Resolve(alert, SloMoved, at);
                        resolved++;
                        alert = null;
                    }
                    if (alert is null)
                    {
                        if (condition != BurnCondition.Burning)
                        {
                            continue;
                        }
                        if (lastResolved.GetValueOrDefault(dedup) is { } resolvedAt && at < resolvedAt + SloBurnAlerts.Cooldown(burn))
                        {
                            continue;
                        }
                        alert = new Alert
                        {
                            TenantId = tenantId, RuleId = rule.Id, DedupKey = dedup, SubjectKey = subjectKey, SubjectType = HealthSubject.Service,
                            SubjectId = serviceId, EnvironmentId = environmentId, Severity = rule.Severity, State = AlertState.Pending, BreachStartedAt = at,
                        };
                        db.Alerts.Add(alert);
                        Transition(alert, null, AlertState.Pending, at, longWindow.BurnRate, "breach");
                        // Both windows over the rate is the confirmation a rule's ForDuration gives: it fires at once.
                        var firing = Transition(alert, AlertState.Pending, AlertState.Firing, at, longWindow.BurnRate, Fired);
                        alert.State = AlertState.Firing;
                        alert.FiredAt = at;
                        AlertChanges.Raise(alert, firing);
                        fired++;
                    }
                    else if (condition == BurnCondition.Cooled)
                    {
                        // Resolved by the short window: its rate is the value the resolve reports, not the long window's memory of the burn.
                        alert.LastValue = shortWindow.BurnRate;
                        Resolve(alert, Cooled, at);
                        resolved++;
                    }
                    alert.LastEvaluatedAt = now;
                    // Held for a window without a rate: keep the last rate known, so a firing alert still shows what fired it.
                    alert.LastValue = (alert.State == AlertState.Resolved ? shortWindow.BurnRate : longWindow.BurnRate) ?? alert.LastValue;
                    alert.LastSamples = longWindow.Total;
                    alert.NoData = condition == BurnCondition.Unknown;
                    alert.Evidence = Evidence(slo, version, definition, burn, longWindow, shortWindow, condition, at);
                }
            }
            // Open alerts of SLOs no longer active (retired, or gone from the file): nothing will ever resolve them otherwise.
            foreach (var alert in open.Values.Where(a => a.State != AlertState.Resolved && !watched.Contains(a.DedupKey)))
            {
                Resolve(alert, SloRetired, at);
                resolved++;
            }
            return new(slos.Count, fired, resolved);
        }

        private sealed record Current(SloVersion Version, SloDefinition Definition, IReadOnlySet<int> SameService);

        // The alert lifecycle's changes: the same history, metric and notification as any alert's.
        private void Resolve(Alert alert, string reason, DateTimeOffset at) => AlertChanges.Resolve(db, alert, reason, at, RuleTemplate.SloBurn);

        private AlertTransition Transition(Alert alert, AlertState? from, AlertState to, DateTimeOffset at, double? value, string reason)
            => AlertChanges.Transition(db, alert, from, to, at, value, reason, RuleTemplate.SloBurn);

        /// <summary>The evidence (Phase 9 plan): which SLO, under which version, and both windows' numbers, enough to see why it fired or held.</summary>
        internal static Dictionary<string, string> Evidence(Slo slo, SloVersion version, SloDefinition definition, BurnAlert burn, WindowStatus longWindow,
            WindowStatus shortWindow, BurnCondition condition, DateTimeOffset at)
        {
            static string N(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
            var evidence = new Dictionary<string, string>
            {
                ["template"] = nameof(RuleTemplate.SloBurn),
                ["slo"] = slo.Key,
                ["sloName"] = definition.Name,
                ["version"] = version.Version.ToString(CultureInfo.InvariantCulture),
                ["category"] = slo.Category.ToString(),
                ["sli"] = slo.Sli,
                ["target"] = N(version.Target),
                ["rate"] = N(burn.Rate),
                ["condition"] = condition.ToString(),
                ["to"] = at.ToString("O"),
            };
            foreach (var (prefix, window) in new[] { ("long", longWindow), ("short", shortWindow) })
            {
                evidence[$"{prefix}Window"] = window.Name;
                evidence[$"{prefix}State"] = window.State;
                evidence[$"{prefix}Total"] = window.Total.ToString(CultureInfo.InvariantCulture);
                evidence[$"{prefix}Bad"] = window.Bad.ToString(CultureInfo.InvariantCulture);
                if (window.BurnRate is { } rate) evidence[$"{prefix}BurnRate"] = N(rate);
            }
            return evidence;
        }
    }
}
