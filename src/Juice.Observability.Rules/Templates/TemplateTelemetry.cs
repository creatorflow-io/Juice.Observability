using System.Data.Common;
using Juice.Observability.Discovery;
using Juice.Observability.Extensibility;

namespace Juice.Observability.Rules
{
    /// <summary>
    /// What a template reaches ClickHouse through (W-23): the platform tenant's window, the workers' profile (the reader's), and only text
    /// that carries the tenant, window and limit placeholders. The tenant, window and limit are the host's: a template can't pass them as parameters.
    /// </summary>
    internal sealed class TemplateTelemetry(ClickHouseTelemetrySource clickHouse, TelemetryWindow window) : ITemplateTelemetry
    {
        private static readonly string[] Placeholders = [TemplateSql.Tenant, TemplateSql.From, TemplateSql.To, TemplateSql.Limit];
        private static readonly string[] Reserved = ["tenant", "from", "to", "limit"];

        /// <summary>The window this instance reads; the built-in templates use it directly.</summary>
        internal TelemetryWindow Window => window;

        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, IReadOnlyDictionary<string, object> parameters, Func<DbDataReader, T> map, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(sql);
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentNullException.ThrowIfNull(map);
            foreach (var placeholder in Placeholders)
            {
                if (!sql.Contains(placeholder, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"A template query must contain {placeholder}", nameof(sql));
                }
            }
            foreach (var name in parameters.Keys)
            {
                if (Reserved.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"The parameter '{name}' is the host's: a template can't set the tenant, window or limit", nameof(parameters));
                }
            }
            return clickHouse.QueryAsync(sql, window, map, ct, parameters);
        }
    }
}
