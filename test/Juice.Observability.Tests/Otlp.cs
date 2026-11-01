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

        private static JsonArray Attributes((string Key, string Value)[] attributes)
            => new(attributes.Select(a => (JsonNode)new JsonObject
            {
                ["key"] = a.Key,
                ["value"] = new JsonObject { ["stringValue"] = a.Value },
            }).ToArray());
    }
}
