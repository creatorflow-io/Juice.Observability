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

        // Modules are trusted in-process code (W-15): this catches mistakes, it does not stop a hostile module. It looks at the code of the query only:
        // string literals are blanked, comments are refused (one could hide or reveal a placeholder), and an OR outside every parenthesis is refused
        // (WHERE tenant AND ... OR 1 = 1 widens the tenant predicate by mistake). Sub-selects and function calls sit inside parentheses.
        private static string CodeOf(string sql)
        {
            var code = new System.Text.StringBuilder(sql.Length);
            var depth = 0;
            for (var i = 0; i < sql.Length; i++)
            {
                var c = sql[i];
                if (c is '\'' or '"' or '`')
                {
                    // A literal or quoted name: skip to its end (a doubled quote or a backslash escapes).
                    for (i++; i < sql.Length && sql[i] != c; i++)
                    {
                        if (sql[i] == '\\') i++;
                    }
                    code.Append(c).Append(c);
                    continue;
                }
                if ((c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') || (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*') || c == '#')
                {
                    throw new ArgumentException("A template query holds no comments", nameof(sql));
                }
                depth += c == '(' ? 1 : c == ')' ? -1 : 0;
                if (depth == 0 && (c is 'o' or 'O') && i + 1 < sql.Length && sql[i + 1] is 'r' or 'R'
                    && (i == 0 || !IsWord(sql[i - 1])) && (i + 2 >= sql.Length || !IsWord(sql[i + 2])))
                {
                    throw new ArgumentException("A template query has no OR outside parentheses: it could widen the tenant predicate", nameof(sql));
                }
                code.Append(c);
            }
            return code.ToString();

            static bool IsWord(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
        }

        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, IReadOnlyDictionary<string, object> parameters, Func<DbDataReader, T> map, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(sql);
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentNullException.ThrowIfNull(map);
            var code = CodeOf(sql);
            foreach (var placeholder in Placeholders)
            {
                if (!code.Contains(placeholder, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"A template query must contain {placeholder}, in its code, not in a comment or a string", nameof(sql));
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
