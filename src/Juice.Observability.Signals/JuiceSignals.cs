using Juice.Observability.Data;

namespace Juice.Observability.Signals
{
    /// <summary>
    /// The Juice signal pack: what the platform reads that only services built on Juice emit, beyond OpenTelemetry's semantic conventions
    /// (docs/signal-packs.md). Every Juice-specific name the platform queries is here and nowhere else, so a change on Juice's side is a
    /// new <see cref="Version"/> of this contract, made in one file.
    /// </summary>
    public static class JuiceSignals
    {
        public const string Name = "juice";

        /// <summary>The contract version: changes when a name below changes or one is added or removed.</summary>
        public const int Version = 1;

        // ─── metrics (cumulative sums in otel_metrics_sum) ───

        /// <summary>Juice.MediatR: requests handled by the mediator, and those that threw.</summary>
        public const string MediatorRequests = "juice.mediator.requests", MediatorErrors = "juice.mediator.errors";

        /// <summary>Juice.Messaging: messages consumed, with <see cref="DispatchResult"/>.</summary>
        public const string MessagesConsumed = "juice.messaging.consumed";

        /// <summary>Juice.Messaging.Outbox.Delivery: delivery attempts, and those that failed.</summary>
        public const string DeliveryAttempts = "delivery_attempt_total", DeliveryFailures = "delivery_failure_total";

        // ─── attributes and spans ───

        /// <summary>On consumed-message metrics and Consumer spans: <c>success</c> or <see cref="DispatchFailure"/>.</summary>
        public const string DispatchResult = "juice.dispatch.result", DispatchFailure = "failure";

        /// <summary>The instrumentation scope of Juice's own spans.</summary>
        public const string Scope = "Juice";

        /// <summary>On a <c>deliver</c> span: what became of the delivery; <see cref="DeliveryPublished"/> when it was sent.</summary>
        public const string DeliveryOutcome = "juice.delivery.outcome", DeliveryPublished = "published";

        // ─── ClickHouse predicates over otel.otel_traces ───

        /// <summary>Juice's span for a message written to the outbox (<c>create {destination}</c>).</summary>
        public const string OutboxCreatedSpan = $"SpanKind = 'Producer' AND SpanName LIKE 'create %' AND ScopeName = '{Scope}'";

        /// <summary>Juice's span for an outbox delivery that was sent (<c>deliver {destination}</c>); its parent is the span that created the message.</summary>
        public const string OutboxDeliveredSpan = $"SpanName LIKE 'deliver %' AND ScopeName = '{Scope}' AND SpanAttributes['{DeliveryOutcome}'] = '{DeliveryPublished}'";

        /// <summary>A Consumer span whose message Juice failed to dispatch, whatever its status.</summary>
        public const string FailedDispatchSpan = $"SpanAttributes['{DispatchResult}'] = '{DispatchFailure}'";

        /// <summary>
        /// The ClickHouse expression that is true for a row of <paramref name="table"/> a Juice service sent: one of Juice's own spans, or one of its
        /// prefixed metrics. The unprefixed delivery counters are left out, since another library may export the same names; a service that
        /// delivers an outbox has Juice's spans anyway.
        /// </summary>
        public static string DetectedIn(string table) => table switch
        {
            "otel_traces" => $"ScopeName = '{Scope}'",
            "otel_metrics_sum" => $"MetricName IN ('{MediatorRequests}', '{MediatorErrors}', '{MessagesConsumed}')",
            _ => "false",
        };

        /// <summary>The rule templates that read only this pack's metrics.</summary>
        public static readonly IReadOnlySet<RuleTemplate> RuleTemplates = new HashSet<RuleTemplate>
        {
            RuleTemplate.MediatorErrors, RuleTemplate.ConsumerFailures, RuleTemplate.DeliveryFailureRate,
        };

        /// <summary>What the pack adds, beyond its rule templates, as <see cref="SignalPackDto.Features"/> names it.</summary>
        public const string OutboxBacklogFeature = "capacity.outbox", FailedDispatchSloFeature = "slo.failed-dispatch";

        internal static SignalPackDto Describe(bool enabled) => new(Name, Version, enabled,
            [.. RuleTemplates.Select(t => t.ToString()).Order(StringComparer.Ordinal)],
            [OutboxBacklogFeature, FailedDispatchSloFeature],
            [MediatorRequests, MediatorErrors, MessagesConsumed, DeliveryAttempts, DeliveryFailures],
            [DispatchResult, DeliveryOutcome],
            [Scope]);
    }
}
