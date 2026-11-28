namespace Juice.Observability.Rules
{
    /// <summary>
    /// The attribute filters of a metric rule as SQL and parameters, shared by every MetricThreshold query (gauge, sum, histogram). The SQL holds only
    /// the filters' count and operators and parameter references (<c>{f0k:String}</c>, <c>{f0v:String}</c> or <c>{f0v:Array(String)}</c>); keys and values
    /// are only ever parameters.
    /// </summary>
    internal static class MetricFilters
    {
        /// <summary>One <c> AND ...</c> condition per filter, on the data point's <c>Attributes</c>.</summary>
        public static string Sql(IReadOnlyList<MetricFilter>? filters) => string.Concat((filters ?? []).Select((f, i) => f.Op == FilterOp.Equals
            ? $" AND Attributes[{{f{i}k:String}}] = {{f{i}v:String}}"
            : $" AND has({{f{i}v:Array(String)}}, Attributes[{{f{i}k:String}}])"));

        /// <summary>Adds <c>f{i}k</c> and <c>f{i}v</c> for each filter to <paramref name="parameters"/>.</summary>
        public static void AddParameters(IReadOnlyList<MetricFilter>? filters, IDictionary<string, object> parameters)
        {
            filters ??= [];
            for (var i = 0; i < filters.Count; i++)
            {
                parameters[$"f{i}k"] = filters[i].Key;
                parameters[$"f{i}v"] = filters[i].Op == FilterOp.Equals ? filters[i].Values[0] : filters[i].Values.ToArray();
            }
        }
    }
}
