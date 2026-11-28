using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Rules;
using Juice.Observability.Signals;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary>
    /// A draft rule's dry evaluation over the last 24 hours (Phase 12 M4, W-22). <c>From</c> and <c>To</c> are the first and last sampled instants;
    /// the rule is evaluated every <c>Step</c>, so the result is approximate. <c>Complete</c> false: the time budget ran out and <c>Cut</c> says where.
    /// </summary>
    public sealed record RulePreviewDto(DateTimeOffset From, DateTimeOffset To, TimeSpan Step, int Points, int PointsEvaluated, bool Complete, string? Cut,
        string? Quiet, string? Unit, RuleDefinition Definition, IReadOnlyList<PreviewSubjectDto> Subjects, bool SubjectsTruncated);

    public sealed record PreviewSubjectDto(string SubjectKey, string SubjectType, int Breaches, int NoData, int WouldOpen, int WouldFire,
        DateTimeOffset? FirstFiredAt, double? Last, double? Min, double? Max, IReadOnlyDictionary<string, string>? LastDetail, IReadOnlyList<PreviewPointDto> Points);

    public sealed record PreviewPointDto(DateTimeOffset At, double? Value, string Condition);

    /// <summary>
    /// <c>POST /rules/preview</c>: the body of a create, validated as one, evaluated at up to 96 instants with the platform's own template queries
    /// (<see cref="RuleTemplates.ObserveAsync"/>) and the alert lifecycle replayed in memory (<see cref="RulePreview"/>). Nothing is written: no rule,
    /// alert, audit record (but a refused role's), change event or idempotency record. Admin or Operator, in the route's tenant; each query takes an
    /// API query slot; the time budget, a per-actor rate and a per-tenant concurrency bound the cost (per replica: each preview is bounded on its own).
    /// </summary>
    public static class RulePreviewEndpoints
    {
        public const double DefaultBudgetSeconds = 25;
        public const int DefaultPerActorPerMinute = 10;
        public const int DefaultConcurrentPerTenant = 2;
        public const int MaxSubjects = 50;
        public const int RateRetrySeconds = 60;
        public const int BusyRetrySeconds = 5;

        /// <summary>The most subjects a preview returns: the row limit (what one evaluation may see), and never more than <see cref="MaxSubjects"/>.</summary>
        internal static int SubjectCap(int rowLimit) => Math.Max(1, Math.Min(rowLimit, MaxSubjects));

        public static RouteGroupBuilder MapRulePreviewEndpoints(this RouteGroupBuilder tenant)
        {
            tenant.MapPost("/rules/preview", PreviewAsync);
            return tenant;
        }

        private static async Task<IResult> PreviewAsync(Guid tenantId, JsonElement body, HttpContext http, PlatformDbContext db, TimeProvider clock,
            IConfiguration configuration, RuleTemplates templates, RuleThresholds thresholds, IOptions<RulesOptions> rules, CancellationToken ct)
        {
            var mutation = new Mutation(http, db, tenantId, "rule.preview", "rule");
            if (await mutation.RequireAsync(null, ct, Roles.Admin, Roles.Operator) is { } denied) return denied;

            var perMinute = Math.Max(1, configuration.GetValue("Rules:Preview:PerActorPerMinute", DefaultPerActorPerMinute));
            var permits = Math.Max(1, configuration.GetValue("Rules:Preview:ConcurrentPerTenant", DefaultConcurrentPerTenant));
            var budgetSeconds = Math.Max(0, configuration.GetValue("Rules:Preview:BudgetSeconds", DefaultBudgetSeconds));

            // An actor the token doesn't name still has one bucket (shared), never none.
            var actor = string.IsNullOrWhiteSpace(mutation.Actor) ? "unknown" : mutation.Actor;
            if (!PreviewLimits.TryTakeRate(tenantId, actor, perMinute, out var wait))
            {
                return TooMany(http, "Too many previews", $"At most {perMinute} previews a minute for each person. Try again in {wait} s.", wait);
            }
            using var slot = PreviewLimits.TryEnter(tenantId, permits);
            if (!slot.IsAcquired)
            {
                return TooMany(http, "Too many previews", $"{permits} previews of this tenant are already running. Try again in a few seconds.", BusyRetrySeconds);
            }

            var (definition, problem, unseen, entry) = await RuleAdminEndpoints.ReadAsync(body, null, http, clock, ct, preview: true);
            if (problem is not null) return problem;
            definition!.Enabled = true;
            var quiet = await SignalCoverage.QuietReasonAsync(db, tenantId, definition, ct) ?? unseen;

            var tenantKey = (string)http.Items[TelemetryEndpoints.TenantKeyItem]!;
            var lag = rules.Value.Lag;
            var end = new DateTimeOffset((clock.GetUtcNow() - lag).UtcTicks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
            var (step, instants) = RulePreview.Plan(definition, end);

            var observed = new List<(DateTimeOffset At, IReadOnlyList<RuleObservation> Observed)>();
            string? cut = null;
            // One budget for the queries and for the threshold provider's calls.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));
            if (unseen is null)
            {
                // The metric hasn't been reported in 24 h, so every instant is no data: nothing to ask ClickHouse.
                foreach (var at in instants)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (budget.IsCancellationRequested) throw new OperationCanceledException(budget.Token);
                        observed.Add((at, await templates.ObserveAsync(definition, new TelemetryWindow(tenantKey, at - definition.Window, at, rules.Value.RowLimit), budget.Token, preview: true)));
                    }
                    catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        cut = string.Create(CultureInfo.InvariantCulture, $"stopped after {observed.Count} of {instants.Count} points");
                        break;
                    }
                }
            }
            else
            {
                observed.AddRange(instants.Select(at => (at, (IReadOnlyList<RuleObservation>)[])));
            }

            var judged = await RulePreview.WithThresholdsAsync(thresholds, definition, observed, budget.Token, ct);
            if (judged.Count < observed.Count)
            {
                // The budget ran out in the provider: what was judged before is the result.
                cut ??= string.Create(CultureInfo.InvariantCulture, $"stopped after {judged.Count} of {instants.Count} points");
            }
            // Each instant is read under the row limit, but the instants see different subjects: the union is capped too.
            var cap = SubjectCap(rules.Value.RowLimit);
            var subjects = RulePreview.Replay(definition, judged, cap, out var truncated);
            if (truncated)
            {
                cut ??= string.Create(CultureInfo.InvariantCulture, $"showing the {cap} subjects that would fire most; others were left out");
            }
            var unit = entry?.Unit is { Length: > 0 } u ? u : null;
            return Results.Ok(new RulePreviewDto(instants[0], instants[^1], step, instants.Count, judged.Count, cut is null, cut, quiet, unit, definition,
                [.. subjects.Select(s => new PreviewSubjectDto(s.SubjectKey, s.SubjectType.ToString(), s.Breaches, s.NoData, s.WouldOpen, s.WouldFire, s.FirstFiredAt,
                    s.Last, s.Min, s.Max, s.LastDetail, [.. s.Points.Select(p => new PreviewPointDto(p.At, p.Value, p.Condition.ToString()))]))], truncated));
        }

        // Not audited: a flood of refusals must not become a flood of database writes.
        private static IResult TooMany(HttpContext http, string title, string detail, int retrySeconds)
        {
            http.Response.Headers.RetryAfter = retrySeconds.ToString(CultureInfo.InvariantCulture);
            return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: title, detail: detail);
        }
    }

    /// <summary>
    /// The preview's limits, in-process and per replica. Keyed by the route's tenant and the token's actor (never by anything in the body), with the
    /// configured limit so a changed setting is a new limiter. No timers: a bucket is topped up when it is used, so an idle one costs only its entry,
    /// swept when there are many.
    /// </summary>
    internal static class PreviewLimits
    {
        private static readonly ConcurrentDictionary<(Guid Tenant, string Actor, int Rate), (TokenBucketRateLimiter Limiter, long Used)> Buckets = new();
        private static readonly ConcurrentDictionary<(Guid Tenant, int Permits), ConcurrencyLimiter> Concurrent = new();
        private const int SweepAbove = 2_000;
        private static readonly long IdleTicks = Stopwatch.Frequency * 600;

        /// <summary>Takes one of the actor's <paramref name="perMinute"/> previews of the tenant; else false, and in how many seconds there is one.</summary>
        public static bool TryTakeRate(Guid tenantId, string actor, int perMinute, out int retryAfterSeconds)
        {
            var now = Stopwatch.GetTimestamp();
            MaybeSweep(now);
            var entry = Buckets.AddOrUpdate((tenantId, actor, perMinute),
                _ => (new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
                {
                    TokenLimit = perMinute, TokensPerPeriod = perMinute, ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = false,
                }), now),
                (_, existing) => (existing.Limiter, now));
            entry.Limiter.TryReplenish();
            using var token = entry.Limiter.AttemptAcquire(1);
            retryAfterSeconds = 0;
            if (token.IsAcquired) return true;
            retryAfterSeconds = token.TryGetMetadata(MetadataName.RetryAfter, out var after) ? (int)Math.Ceiling(after.TotalSeconds) : RulePreviewEndpoints.RateRetrySeconds;
            return false;
        }

        private static long _lastSweep;
        private static int _sweeps;

        /// <summary>Sweeps that ran (for tests).</summary>
        internal static int Sweeps => Volatile.Read(ref _sweeps);

        internal static bool HasBucket(Guid tenantId, string actor, int perMinute) => Buckets.ContainsKey((tenantId, actor, perMinute));

        /// <summary>At most once a minute, and only when there are many buckets, one request sweeps; the others go on without scanning.</summary>
        internal static void MaybeSweep(long now)
        {
            if (Buckets.Count <= SweepAbove) return;
            var last = Volatile.Read(ref _lastSweep);
            if (now - last < Stopwatch.Frequency * 60 && last != 0) return;
            if (Interlocked.CompareExchange(ref _lastSweep, now, last) != last) return;
            Interlocked.Increment(ref _sweeps);
            Sweep(now);
        }

        /// <summary>
        /// Drops the buckets idle for ten minutes (full again, so nothing is lost). An entry goes only if it is still the value that was seen: one
        /// used since is a new value and stays. Not disposed: with no timer a limiter holds no resource.
        /// </summary>
        internal static void Sweep(long now)
        {
            foreach (var entry in Buckets)
            {
                if (now - entry.Value.Used > IdleTicks) Buckets.TryRemove(entry);
            }
        }

        /// <summary>One of the tenant's <paramref name="permits"/> previews at once; dispose it to give it back. Not acquired when they are all running.</summary>
        public static RateLimitLease TryEnter(Guid tenantId, int permits)
            => Concurrent.GetOrAdd((tenantId, permits), key => new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = key.Permits, QueueLimit = 0 })).AttemptAcquire(1);
    }
}
