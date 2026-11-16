using System.Globalization;
using System.Text.RegularExpressions;
using static Juice.Observability.Api.PlatformSampler;

namespace Juice.Observability.Api
{
    /// <summary>
    /// Reads the Collector's own metrics page (Prometheus text, from its <c>service.telemetry</c> reader) for the Overview's ingestion figures
    /// (Phase 11 R8): per signal, what the tenant's receivers accepted and refused and what the exporters dropped, and each exporter's queue
    /// against its bound. The platform's own pipeline (<c>otlp/platform</c>, <c>prometheus/self</c>) is left out of accepted and refused: it is
    /// not the tenant's load. The exporters are shared, so what they drop is counted whichever pipeline it came from.
    /// </summary>
    public static partial class CollectorMetrics
    {
        /// <summary>The receivers of the platform's own telemetry (Phase 11 R1), not counted as the tenant's load.</summary>
        public static readonly IReadOnlySet<string> PlatformReceivers = new HashSet<string>(StringComparer.Ordinal) { "otlp/platform", "prometheus/self" };

        /// <summary>The Collector's names for each signal's items, and the Overview's.</summary>
        private static readonly IReadOnlyDictionary<string, string> Signals = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["spans"] = "spans", ["metric_points"] = "points", ["log_records"] = "logs",
        };

        [GeneratedRegex(@"^(?<name>[a-zA-Z_:][a-zA-Z0-9_:]*)(\{(?<labels>[^}]*)\})?\s+(?<value>\S+)")]
        private static partial Regex Line();

        [GeneratedRegex(@"(?<key>[a-zA-Z_][a-zA-Z0-9_]*)=""(?<value>(?:[^""\\]|\\.)*)""")]
        private static partial Regex Label();

        /// <summary>Each signal's totals and each exporter's queue, from the page's text.</summary>
        public static (IReadOnlyList<SignalFlow> Signals, IReadOnlyList<ExportQueue> Queues) Parse(string text)
        {
            var totals = Signals.Values.ToDictionary(s => s, _ => (Accepted: 0d, Refused: 0d, Dropped: 0d), StringComparer.Ordinal);
            var sizes = new Dictionary<string, double>(StringComparer.Ordinal);
            var capacities = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || Line().Match(line) is not { Success: true } m
                    || !double.TryParse(m.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    continue;
                }
                var name = m.Groups["name"].Value;
                if (name.EndsWith("_total", StringComparison.Ordinal)) name = name[..^"_total".Length];
                var labels = Label().Matches(m.Groups["labels"].Value).ToDictionary(l => l.Groups["key"].Value, l => l.Groups["value"].Value, StringComparer.Ordinal);
                if (name is "otelcol_exporter_queue_size" or "otelcol_exporter_queue_capacity" && labels.TryGetValue("exporter", out var exporter))
                {
                    var into = name.EndsWith("size", StringComparison.Ordinal) ? sizes : capacities;
                    into[exporter] = into.GetValueOrDefault(exporter) + value;
                    continue;
                }
                foreach (var (item, signal) in Signals)
                {
                    var t = totals[signal];
                    if (name == $"otelcol_receiver_accepted_{item}" && !PlatformReceiver(labels)) t.Accepted += value;
                    else if (name == $"otelcol_receiver_refused_{item}" && !PlatformReceiver(labels)) t.Refused += value;
                    else if (name == $"otelcol_exporter_enqueue_failed_{item}" || name == $"otelcol_exporter_send_failed_{item}") t.Dropped += value;
                    else continue;
                    totals[signal] = t;
                }
            }
            return ([.. totals.Select(t => new SignalFlow(t.Key, t.Value.Accepted, t.Value.Refused, t.Value.Dropped))],
                [.. sizes.Keys.Union(capacities.Keys).Order(StringComparer.Ordinal)
                    .Select(e => new ExportQueue(e, sizes.GetValueOrDefault(e), capacities.GetValueOrDefault(e)))]);
        }

        private static bool PlatformReceiver(Dictionary<string, string> labels)
            => labels.TryGetValue("receiver", out var receiver) && PlatformReceivers.Contains(receiver);

        /// <summary>
        /// Each signal's rates since <paramref name="previous"/>, <paramref name="seconds"/> ago. None when there is no previous sample, or a
        /// count went down (the Collector restarted): a rate across a restart would be wrong.
        /// </summary>
        public static IReadOnlyList<SignalFlow> WithRates(IReadOnlyList<SignalFlow> now, IReadOnlyList<SignalFlow>? previous, double? seconds)
        {
            if (previous is null || seconds is not > 0)
            {
                return now;
            }
            var before = previous.ToDictionary(f => f.Signal, StringComparer.Ordinal);
            if (now.Any(f => before.TryGetValue(f.Signal, out var b) && (f.Accepted < b.Accepted || f.Refused < b.Refused || f.Dropped < b.Dropped)))
            {
                return now;
            }
            return [.. now.Select(f => before.TryGetValue(f.Signal, out var b)
                ? f with
                {
                    AcceptedPerSecond = (f.Accepted - b.Accepted) / seconds, RefusedPerSecond = (f.Refused - b.Refused) / seconds,
                    DroppedPerSecond = (f.Dropped - b.Dropped) / seconds,
                }
                : f)];
        }
    }
}
