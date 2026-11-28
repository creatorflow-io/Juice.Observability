using System.Globalization;
using System.Text.RegularExpressions;
using Juice.Observability.Extensibility;

namespace Juice.Observability.Api.Evidence
{
    /// <summary>What a section looks like after the core's caps: <see cref="Section"/> is null when it was invalid.</summary>
    internal sealed record SanitizedSection(EvidenceSection? Section, bool Truncated);

    /// <summary>
    /// The core's bounds on what a contributor returns: text is plain (no control, line-separator or bidirectional characters, no broken surrogate pairs),
    /// titles and labels are cut at 64, values and notes at 512, items at 50, and a link survives only when it points to a manifest route. A section
    /// that breaks a rule of the text is dropped whole, never repaired.
    /// </summary>
    internal static partial class EvidenceSanitizer
    {
        public const int MaxItems = 50, MaxLabel = 64, MaxValue = 512, MaxQueryEntries = 8, MaxQueryKey = 32, MaxQueryValue = 128;

        private static readonly HashSet<string> Severities = new(StringComparer.Ordinal) { "info", "warning", "critical" };

        [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._~-]{0,127}(/[A-Za-z0-9][A-Za-z0-9._~-]{0,127}){0,7}\z", RegexOptions.CultureInvariant)]
        private static partial Regex RoutePath();

        [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant)]
        private static partial Regex QueryKey();

        public static SanitizedSection Check(EvidenceSection section, Func<string, bool> routeMatches)
        {
            var truncated = false;
            if (section.Items is null || !Text(section.Title, MaxLabel, required: true, ref truncated, out var title) || !Text(section.Note, MaxValue, required: false, ref truncated, out var note))
            {
                return new SanitizedSection(null, false);
            }
            var items = new List<EvidenceItem>();
            foreach (var item in section.Items)
            {
                if (items.Count == MaxItems)
                {
                    truncated = true;
                    break;
                }
                if (item is null || !Text(item.Label, MaxLabel, required: true, ref truncated, out var label) || !Text(item.Value, MaxValue, required: false, ref truncated, out var value)
                    || item.Severity is not null && !Severities.Contains(item.Severity))
                {
                    return new SanitizedSection(null, false);
                }
                items.Add(new EvidenceItem(label!, value, item.At, Link(item.Link, routeMatches), item.Severity));
            }
            return new SanitizedSection(new EvidenceSection(title!, items, note), truncated);
        }

        /// <summary>The link when its route is a concrete relative path that matches a manifest route and its query is small and plain; null otherwise.</summary>
        private static EvidenceLink? Link(EvidenceLink? link, Func<string, bool> routeMatches)
        {
            if (link?.Route is not { } route || !RoutePath().IsMatch(route) || route.Contains("..", StringComparison.Ordinal) || !routeMatches(route))
            {
                return null;
            }
            if (link.Query is null || link.Query.Count == 0)
            {
                return new EvidenceLink(route);
            }
            if (link.Query.Count > MaxQueryEntries || link.Query.Any(q => q.Key is null || !QueryKey().IsMatch(q.Key) || q.Value is null || q.Value.Length > MaxQueryValue || !Plain(q.Value)))
            {
                return null;
            }
            return new EvidenceLink(route, new Dictionary<string, string>(link.Query));
        }

        /// <summary>False when the text breaks a rule (the section is invalid); cuts to <paramref name="max"/> and sets <paramref name="truncated"/> otherwise.</summary>
        private static bool Text(string? text, int max, bool required, ref bool truncated, out string? result)
        {
            result = null;
            if (text is null)
            {
                return !required;
            }
            if (!Plain(text) || required && string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            if (text.Length > max)
            {
                // Never cut inside a surrogate pair.
                var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
                text = text[..cut];
                truncated = true;
            }
            result = text;
            return true;
        }

        /// <summary>
        /// For text the core takes from its own data (a change summary, a source) rather than a contributor's: control and line characters become a space, and format
        /// characters (bidirectional marks, zero-width joiners, soft hyphens), private-use and unassigned ones and unpaired surrogates are removed, so
        /// <see cref="Plain"/> holds. An item is cleaned, not the section dropped.
        /// </summary>
        internal static string Clean(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }
            var result = new System.Text.StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    result.Append(c).Append(text[++i]);
                    continue;
                }
                if (char.IsSurrogate(c))
                {
                    continue;
                }
                switch (char.GetUnicodeCategory(c))
                {
                    case UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator:
                        result.Append(' ');
                        break;
                    case UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned:
                        break;
                    default:
                        result.Append(c);
                        break;
                }
            }
            return result.ToString().Trim();
        }

        /// <summary>No control, format (bidirectional marks, zero-width joiners), line or paragraph separator character, and no unpaired surrogate.</summary>
        internal static bool Plain(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                    {
                        return false;
                    }
                    i++;
                    continue;
                }
                if (char.IsLowSurrogate(c) || char.IsControl(c))
                {
                    return false;
                }
                if (char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned
                    or UnicodeCategory.PrivateUse)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
