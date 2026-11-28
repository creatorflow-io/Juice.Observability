using System.Text.Json.Nodes;

namespace Juice.Observability.Tests
{
    /// <summary>Builds OTLP/JSON metric payloads: gauges, sums of either temporality and monotonicity, explicit-bucket histograms.</summary>
    public static class OtlpMetrics
    {
        public const int Delta = 1, Cumulative = 2;

        private static string Nanos(DateTimeOffset t) => (t.ToUnixTimeMilliseconds() * 1_000_000).ToString();

        /// <summary>A gauge with one point per (time, value).</summary>
        public static JsonObject Gauge((string Key, string Value)[] resource, string name, (string Key, string Value)[] attributes,
            params (DateTimeOffset At, double Value)[] points)
            => Wrap(resource, name, "gauge", new JsonObject { ["dataPoints"] = new JsonArray(points.Select(p => (JsonNode)new JsonObject
            {
                ["asDouble"] = p.Value,
                ["timeUnixNano"] = Nanos(p.At),
                ["attributes"] = Attributes(attributes),
            }).ToArray()) });

        /// <summary>A sum with one point per (time, value), all sharing <paramref name="start"/> (a new start is a restart).</summary>
        public static JsonObject Sum((string Key, string Value)[] resource, string name, int temporality, bool monotonic, DateTimeOffset start,
            (string Key, string Value)[] attributes, params (DateTimeOffset At, double Value)[] points)
            => Wrap(resource, name, "sum", new JsonObject
            {
                ["aggregationTemporality"] = temporality,
                ["isMonotonic"] = monotonic,
                ["dataPoints"] = new JsonArray(points.Select(p => (JsonNode)new JsonObject
                {
                    ["asDouble"] = p.Value,
                    ["startTimeUnixNano"] = Nanos(start),
                    ["timeUnixNano"] = Nanos(p.At),
                    ["attributes"] = Attributes(attributes),
                }).ToArray()),
            });

        /// <summary>An explicit-bucket histogram: <c>counts</c> has one more entry than <c>bounds</c> (the overflow bucket).</summary>
        public static JsonObject BucketHistogram((string Key, string Value)[] resource, string name, int temporality, DateTimeOffset start,
            (string Key, string Value)[] attributes, double[] bounds, params (DateTimeOffset At, long[] Counts, double Sum)[] points)
            => Wrap(resource, name, "histogram", new JsonObject
            {
                ["aggregationTemporality"] = temporality,
                ["dataPoints"] = new JsonArray(points.Select(p => (JsonNode)new JsonObject
                {
                    ["count"] = p.Counts.Sum().ToString(),
                    ["sum"] = p.Sum,
                    ["bucketCounts"] = new JsonArray(p.Counts.Select(c => (JsonNode)c.ToString()).ToArray()),
                    ["explicitBounds"] = new JsonArray(bounds.Select(b => (JsonNode)b).ToArray()),
                    ["startTimeUnixNano"] = Nanos(start),
                    ["timeUnixNano"] = Nanos(p.At),
                    ["attributes"] = Attributes(attributes),
                }).ToArray()),
            });

        /// <summary>The same payload with the metric's unit set (the builders send none).</summary>
        public static JsonObject WithUnit(JsonObject payload, string unit)
        {
            payload["resourceMetrics"]![0]!["scopeMetrics"]![0]!["metrics"]![0]!["unit"] = unit;
            return payload;
        }

        private static JsonObject Wrap((string Key, string Value)[] resource, string name, string kind, JsonObject body) => new()
        {
            ["resourceMetrics"] = new JsonArray(new JsonObject
            {
                ["resource"] = new JsonObject { ["attributes"] = Attributes(resource) },
                ["scopeMetrics"] = new JsonArray(new JsonObject
                {
                    ["scope"] = new JsonObject { ["name"] = "Juice.Observability.Tests" },
                    ["metrics"] = new JsonArray(new JsonObject { ["name"] = name, ["unit"] = "", [kind] = body }),
                }),
            }),
        };

        private static JsonArray Attributes((string Key, string Value)[] attributes)
            => new(attributes.Select(a => (JsonNode)new JsonObject
            {
                ["key"] = a.Key,
                ["value"] = new JsonObject { ["stringValue"] = a.Value },
            }).ToArray());
    }
}
