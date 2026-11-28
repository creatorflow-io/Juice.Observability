using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace Juice.Observability.Api.Integrations
{
    /// <summary>
    /// Checks a config or scope value against the plugin's typed field list (decision 3): unknown properties, a type, range, length, pattern or required-field
    /// miss, and the size of the whole value. Errors are keyed <c>&lt;prefix&gt;.&lt;field&gt;</c> for the 400 problem's <c>errors</c>.
    /// </summary>
    internal static class IntegrationConfigValidator
    {
        public const int MaxConfigBytes = 16 * 1024, MaxScopeBytes = 4 * 1024, DefaultMaxLength = 1000, MaxListItems = 100;

        public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

        /// <summary>Empty when valid. <paramref name="value"/> must be an object (an absent scope is <c>{}</c>).</summary>
        public static Dictionary<string, List<string>> Validate(IntegrationConfigSchema schema, JsonElement value, string prefix, int maxBytes)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            void Add(string key, string message)
            {
                if (!errors.TryGetValue(key, out var list))
                {
                    errors[key] = list = [];
                }
                list.Add(message);
            }

            if (value.ValueKind != JsonValueKind.Object)
            {
                Add(prefix, "must be an object");
                return errors;
            }
            if (Encoding.UTF8.GetByteCount(value.GetRawText()) > maxBytes)
            {
                Add(prefix, $"is over {maxBytes / 1024} KB");
                return errors;
            }
            var fields = schema.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!fields.ContainsKey(property.Name))
                {
                    Add($"{prefix}.{property.Name}", "is not a property of this plugin");
                }
            }
            foreach (var field in schema.Fields)
            {
                var key = $"{prefix}.{field.Name}";
                if (!value.TryGetProperty(field.Name, out var item) || item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    if (field.Required)
                    {
                        Add(key, "is required");
                    }
                    continue;
                }
                foreach (var message in Check(field, item))
                {
                    Add(key, message);
                }
            }
            return errors;
        }

        private static IEnumerable<string> Check(IntegrationConfigField field, JsonElement item)
        {
            switch (field.Kind)
            {
                case IntegrationFieldKind.String:
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        yield return "must be a string";
                        break;
                    }
                    foreach (var message in Text(field, item.GetString()!))
                    {
                        yield return message;
                    }
                    break;
                case IntegrationFieldKind.Integer:
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var whole))
                    {
                        yield return "must be an integer";
                        break;
                    }
                    foreach (var message in Range(field, whole))
                    {
                        yield return message;
                    }
                    break;
                case IntegrationFieldKind.Number:
                    if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var number) || !double.IsFinite(number))
                    {
                        yield return "must be a number";
                        break;
                    }
                    foreach (var message in Range(field, number))
                    {
                        yield return message;
                    }
                    break;
                case IntegrationFieldKind.Boolean:
                    if (item.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        yield return "must be true or false";
                    }
                    break;
                case IntegrationFieldKind.Enum:
                    if (item.ValueKind != JsonValueKind.String || field.Allowed?.Contains(item.GetString()!, StringComparer.Ordinal) != true)
                    {
                        yield return $"must be one of {string.Join(", ", field.Allowed ?? [])}";
                    }
                    break;
                case IntegrationFieldKind.StringList:
                    if (item.ValueKind != JsonValueKind.Array)
                    {
                        yield return "must be a list of strings";
                        break;
                    }
                    var count = item.GetArrayLength();
                    if (count > (field.MaxItems ?? MaxListItems))
                    {
                        yield return $"has more than {field.MaxItems ?? MaxListItems} items";
                    }
                    if (item.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.String))
                    {
                        yield return "must be a list of strings";
                        break;
                    }
                    foreach (var message in item.EnumerateArray().SelectMany(i => Text(field, i.GetString()!)).Distinct())
                    {
                        yield return message;
                    }
                    break;
                case IntegrationFieldKind.Duration:
                    if (item.ValueKind != JsonValueKind.String || !TryDuration(item.GetString()!, out var duration))
                    {
                        yield return "must be a duration such as 00:05:00";
                        break;
                    }
                    // Min and Max of a duration are seconds.
                    foreach (var message in Range(field, duration.TotalSeconds, " seconds"))
                    {
                        yield return message;
                    }
                    break;
            }
        }

        /// <summary><c>hh:mm:ss</c> (or <c>d.hh:mm:ss</c>), or an ISO 8601 duration such as <c>PT5M</c>; not negative.</summary>
        public static bool TryDuration(string text, out TimeSpan duration)
        {
            duration = default;
            if (text.Length is 0 or > 32)
            {
                return false;
            }
            try
            {
                duration = text.StartsWith('P') ? XmlConvert.ToTimeSpan(text) : TimeSpan.ParseExact(text, "c", CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is FormatException or OverflowException)
            {
                return false;
            }
            return duration >= TimeSpan.Zero;
        }

        private static IEnumerable<string> Text(IntegrationConfigField field, string text)
        {
            if (text.Length > (field.MaxLength ?? DefaultMaxLength))
            {
                yield return $"is longer than {field.MaxLength ?? DefaultMaxLength} characters";
            }
            if (field.Pattern is not null)
            {
                bool matches;
                try
                {
                    matches = Regex.IsMatch(text, field.Pattern, RegexOptions.CultureInvariant, PatternTimeout);
                }
                catch (RegexMatchTimeoutException)
                {
                    matches = false;
                }
                if (!matches)
                {
                    yield return "has the wrong format";
                }
            }
        }

        private static IEnumerable<string> Range(IntegrationConfigField field, double value, string unit = "")
        {
            if (field.Min is { } min && value < min)
            {
                yield return $"must be at least {min.ToString(CultureInfo.InvariantCulture)}{unit}";
            }
            if (field.Max is { } max && value > max)
            {
                yield return $"must be at most {max.ToString(CultureInfo.InvariantCulture)}{unit}";
            }
        }
    }
}
