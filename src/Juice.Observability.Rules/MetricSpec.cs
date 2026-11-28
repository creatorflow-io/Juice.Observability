using System.Text.Json.Serialization;

namespace Juice.Observability.Rules
{
    /// <summary>Counter = monotonic sum; UpDownCounter = non-monotonic sum.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MetricKind { Gauge, Counter, UpDownCounter, Histogram }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MetricAggregation { Last, Avg, Max, Min, Rate, Increase, P50, P90, P95, P99 }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MetricSubject { Service, Host }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum Comparison { Above, Below }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum FilterOp { Equals, In }

    /// <summary>A data-point attribute filter. Key and values reach ClickHouse only as typed parameters.</summary>
    public sealed class MetricFilter
    {
        public string Key { get; set; } = "";
        public FilterOp Op { get; set; }
        private List<string> _values = [];

        /// <summary>Never null: a null in a body or file is read as no values.</summary>
        public List<string> Values { get => _values; set => _values = value ?? []; }
    }

    /// <summary>What a <see cref="Juice.Observability.Data.RuleTemplate.MetricThreshold"/> rule watches: one metric, one aggregation, optional attribute filters.</summary>
    public sealed class MetricSpec
    {
        /// <summary>A metric name (also the Telemetry API's).</summary>
        public const string NamePattern = @"^[A-Za-z_:][A-Za-z0-9_.:/\-]{0,254}$";
        public const string KeyPattern = @"^[A-Za-z_][A-Za-z0-9_.\-/]{0,127}$";
        public const int MaxFilters = 5, MaxValues = 20, MaxValueLength = 256;

        public string Name { get; set; } = "";
        /// <summary>Required after the API fills it from the catalog, and in rules.json.</summary>
        public MetricKind? Kind { get; set; }
        public MetricAggregation Aggregation { get; set; }
        private List<MetricFilter> _filters = [];

        /// <summary>Never null: a null in a body or file is read as no filters.</summary>
        public List<MetricFilter> Filters { get => _filters; set => _filters = value ?? []; }
        public MetricSubject Subject { get; set; } = MetricSubject.Service;
        public Comparison Comparison { get; set; } = Comparison.Above;
    }
}
