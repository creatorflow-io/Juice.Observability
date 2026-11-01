using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Juice.Observability.Tests
{
    /// <summary>Builds OTLP/JSON trace payloads.</summary>
    public static class Otlp
    {
        public const int Internal = 1, Server = 2, Client = 3, Producer = 4, Consumer = 5;

        public static string TraceId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        public static string SpanId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

        public static JsonObject Span(string traceId, string spanId, string? parentSpanId, string name, int kind, DateTimeOffset at,
            params (string Key, string Value)[] attributes)
        {
            var nanos = (at.ToUnixTimeMilliseconds() * 1_000_000).ToString();
            return new JsonObject
            {
                ["traceId"] = traceId,
                ["spanId"] = spanId,
                ["parentSpanId"] = parentSpanId ?? "",
                ["name"] = name,
                ["kind"] = kind,
                ["startTimeUnixNano"] = nanos,
                ["endTimeUnixNano"] = nanos,
                ["attributes"] = Attributes(attributes),
            };
        }

        /// <summary>Marks a span as failed (status code ERROR).</summary>
        public static JsonObject Failed(JsonObject span)
        {
            span["status"] = new JsonObject { ["code"] = 2 };
            return span;
        }

        /// <summary><paramref name="count"/> server spans at <paramref name="at"/>, the first <paramref name="failed"/> of them failed.</summary>
        public static JsonObject[] Requests(int count, int failed, DateTimeOffset at)
            => Enumerable.Range(0, count).Select(i =>
            {
                var span = Span(TraceId(), SpanId(), null, "GET /orders", Server, at);
                return i < failed ? Failed(span) : span;
            }).ToArray();

        /// <summary>A cumulative monotonic sum with one point per (time, value), all sharing one start time.</summary>
        public static JsonObject Sum((string Key, string Value)[] resource, string name, DateTimeOffset start,
            (string Key, string Value)[] attributes, params (DateTimeOffset At, long Value)[] points)
        {
            static string Nanos(DateTimeOffset t) => (t.ToUnixTimeMilliseconds() * 1_000_000).ToString();
            return new JsonObject
            {
                ["resourceMetrics"] = new JsonArray(new JsonObject
                {
                    ["resource"] = new JsonObject { ["attributes"] = Attributes(resource) },
                    ["scopeMetrics"] = new JsonArray(new JsonObject
                    {
                        ["scope"] = new JsonObject { ["name"] = "Juice.Observability.Tests" },
                        ["metrics"] = new JsonArray(new JsonObject
                        {
                            ["name"] = name,
                            ["sum"] = new JsonObject
                            {
                                ["aggregationTemporality"] = 2,
                                ["isMonotonic"] = true,
                                ["dataPoints"] = new JsonArray(points.Select(p => (JsonNode)new JsonObject
                                {
                                    ["asInt"] = p.Value.ToString(),
                                    ["startTimeUnixNano"] = Nanos(start),
                                    ["timeUnixNano"] = Nanos(p.At),
                                    ["attributes"] = Attributes(attributes),
                                }).ToArray()),
                            },
                        }),
                    }),
                }),
            };
        }

        /// <summary>A span lasting <paramref name="duration"/>.</summary>
        public static JsonObject Lasting(JsonObject span, TimeSpan duration)
        {
            var start = long.Parse((string)span["startTimeUnixNano"]!);
            span["endTimeUnixNano"] = (start + (long)(duration.TotalMilliseconds * 1_000_000)).ToString();
            return span;
        }

        /// <summary>One resource with its spans.</summary>
        public static JsonObject Traces((string Key, string Value)[] resource, params JsonObject[] spans) => new()
        {
            ["resourceSpans"] = new JsonArray(new JsonObject
            {
                ["resource"] = new JsonObject { ["attributes"] = Attributes(resource) },
                ["scopeSpans"] = new JsonArray(new JsonObject
                {
                    ["scope"] = new JsonObject { ["name"] = "Juice.Observability.Tests" },
                    ["spans"] = new JsonArray(spans),
                }),
            }),
        };

        /// <summary>One resource with its log records: (time, severity number, body, trace id, span id).</summary>
        public static JsonObject Logs((string Key, string Value)[] resource,
            params (DateTimeOffset At, int Severity, string Body, string? TraceId, string? SpanId)[] records) => new()
        {
            ["resourceLogs"] = new JsonArray(new JsonObject
            {
                ["resource"] = new JsonObject { ["attributes"] = Attributes(resource) },
                ["scopeLogs"] = new JsonArray(new JsonObject
                {
                    ["scope"] = new JsonObject { ["name"] = "Juice.Observability.Tests" },
                    ["logRecords"] = new JsonArray(records.Select(r => (JsonNode)new JsonObject
                    {
                        ["timeUnixNano"] = (r.At.ToUnixTimeMilliseconds() * 1_000_000).ToString(),
                        ["severityNumber"] = r.Severity,
                        ["body"] = new JsonObject { ["stringValue"] = r.Body },
                        ["traceId"] = r.TraceId ?? "",
                        ["spanId"] = r.SpanId ?? "",
                    }).ToArray()),
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
