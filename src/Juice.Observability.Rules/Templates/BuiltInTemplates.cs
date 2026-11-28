using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Extensibility;

namespace Juice.Observability.Rules
{
    /// <summary>A built-in template's validation, which <see cref="RuleDefinition.Validate"/> calls without a round trip through <see cref="RuleSpec"/>.</summary>
    internal interface IBuiltInRuleTemplate : IRuleTemplate
    {
        /// <summary>The errors that belong to this template, without the rule's name in front.</summary>
        IEnumerable<string> ValidateDefinition(RuleDefinition rule);
    }

    /// <summary>
    /// The core's templates, on the registry: an adapter over the query methods of <see cref="RuleTemplates"/> (the SQL stays there) and over the
    /// template's own validation, with the messages <see cref="RuleDefinition.Validate"/> had. SloBurn is not here: its alerts come from
    /// <see cref="IAlertSource"/>, not from a query.
    /// </summary>
    internal static class BuiltInTemplates
    {
        private delegate Task<IReadOnlyList<RuleObservation>> Observe(RuleTemplates queries, RuleDefinition rule, TelemetryWindow window,
            Dictionary<string, object> scope, CancellationToken ct);

        /// <summary><paramref name="queries"/> null gives templates that validate but observe nothing (a registry for validation only).</summary>
        public static IReadOnlyList<IRuleTemplate> Create(RuleTemplates? queries) =>
        [
            new Adapter(queries, new(nameof(RuleTemplate.ErrorRate), "Error rate", TemplateSubjectKind.Service, UsesSamples: true, ThresholdUnit: "ratio"),
                (q, r, w, s, ct) => q.ObserveErrorRateAsync(w, s, ct), Ratio),
            new Adapter(queries, new(nameof(RuleTemplate.LatencyP95), "Latency p95", TemplateSubjectKind.Service, UsesSamples: true, ThresholdUnit: "ms"),
                (q, r, w, s, ct) => q.ObserveLatencyP95Async(w, s, ct),
                r => r.Threshold <= 0 || r.Recovery <= 0 ? ["latency thresholds are positive milliseconds"] : []),
            new Adapter(queries, new(nameof(RuleTemplate.ThroughputAbsent), "Throughput absent", TemplateSubjectKind.Service, FiresAbove: false, ThresholdUnit: "requests"),
                (q, r, w, s, ct) => q.ObserveThroughputAbsentAsync(w, s, ct),
                r => r.Threshold < 0 || r.Recovery < r.Threshold ? ["throughput thresholds are request counts, recovery at least the threshold"] : []),
            new Adapter(queries, new(nameof(RuleTemplate.DeliveryFailureRate), "Delivery failure rate", TemplateSubjectKind.Service, UsesSamples: true, ThresholdUnit: "ratio"),
                (q, r, w, s, ct) => q.ObserveDeliveryFailureRateAsync(w, s, ct), Ratio),
            new Adapter(queries, new(nameof(RuleTemplate.HostMemory), "Host memory", TemplateSubjectKind.Host, ThresholdUnit: "ratio"),
                (q, r, w, s, ct) => q.ObserveHostMemoryAsync(w, s, ct), Ratio),
            new Adapter(queries, new(nameof(RuleTemplate.MediatorErrors), "Mediator errors", TemplateSubjectKind.Service, ThresholdUnit: "count"),
                (q, r, w, s, ct) => q.ObserveMediatorErrorsAsync(w, s, ct), Count),
            new Adapter(queries, new(nameof(RuleTemplate.ConsumerFailures), "Consumer failures", TemplateSubjectKind.Service, ThresholdUnit: "count"),
                (q, r, w, s, ct) => q.ObserveConsumerFailuresAsync(w, s, ct), Count),
            new Adapter(queries, new(nameof(RuleTemplate.MetricThreshold), "Metric threshold", TemplateSubjectKind.Either, UsesSamples: true),
                (q, r, w, s, ct) => q.ObserveMetricThresholdAsync(r, w, s, ct), Metric),
            new Adapter(queries, new(nameof(RuleTemplate.HostCpu), "Host CPU", TemplateSubjectKind.Host, ThresholdUnit: "ratio"),
                (q, r, w, s, ct) => q.ObserveCpuAsync(w, s, ct), Ratio),
            new Adapter(queries, new(nameof(RuleTemplate.HostDisk), "Host disk", TemplateSubjectKind.Host, ThresholdUnit: "ratio"),
                (q, r, w, s, ct) => q.ObserveDiskAsync(w, s, ct), Ratio),
        ];

        private static IEnumerable<string> Ratio(RuleDefinition r)
            => r.Threshold is < 0 or > 1 || r.Recovery is < 0 or > 1 ? ["Threshold and RecoveryThreshold are ratios from 0 to 1"] : [];

        private static IEnumerable<string> Count(RuleDefinition r)
            => r.Threshold < 0 || r.Recovery < 0 ? ["error thresholds are counts, not negative"] : [];

        private static IEnumerable<string> Metric(RuleDefinition r)
        {
            if (!double.IsFinite(r.Threshold) || !double.IsFinite(r.Recovery)) yield return "Threshold and RecoveryThreshold are finite numbers";
            if (!r.FiresAbove && r.Recovery < r.Threshold) yield return "RecoveryThreshold must not be below Threshold when Comparison is Below";
            if (r.Metric is null) yield return "Metric is required for a MetricThreshold rule";
            else foreach (var error in RuleDefinition.ValidateMetric(r.Metric)) yield return error;
        }

        private sealed class Adapter(RuleTemplates? queries, RuleTemplateInfo info, Observe observe, Func<RuleDefinition, IEnumerable<string>> validate) : IBuiltInRuleTemplate
        {
            public RuleTemplateInfo Info => info;

            public IEnumerable<string> ValidateDefinition(RuleDefinition rule)
            {
                foreach (var error in validate(rule)) yield return error;
                if (info.Key != nameof(RuleTemplate.MetricThreshold) && rule.Metric is not null) yield return "only MetricThreshold rules take Metric";
            }

            public IEnumerable<string> Validate(RuleSpec rule) => ValidateDefinition(RuleDefinition.FromJson(rule.Definition.GetRawText()));

            public async Task<IReadOnlyList<TemplateObservation>> ObserveAsync(RuleSpec rule, ITemplateTelemetry telemetry, TemplateWindow window, CancellationToken ct)
            {
                if (queries is null) throw new InvalidOperationException("This registry validates rules only; it has no telemetry behind it");
                if (telemetry is not TemplateTelemetry core) throw new InvalidOperationException("The core's templates read through the host's own telemetry");
                var definition = RuleDefinition.FromJson(rule.Definition.GetRawText());
                var observations = await observe(queries, definition, core.Window, RuleTemplates.Scope(definition), ct);
                return [.. observations.Select(o => new TemplateObservation(o.SubjectKey, o.SubjectType == HealthSubject.Host ? AlertSubjectKind.Host : AlertSubjectKind.Service,
                    o.ServiceNamespace, o.ServiceName, o.Environment, o.Host, o.Value, o.Samples, o.Detail))];
            }
        }
    }
}
