using System.Text.RegularExpressions;

namespace Juice.Observability.Api.Endpoints
{
    /// <summary>
    /// What a caller reports about a change is untrusted text that may hold secrets, such as a deploy command pasted into a summary.
    /// Attributes are deny by default: only <see cref="AllowedAttributes"/> are kept. Free text keeps its meaning with secrets masked.
    /// This catches common shapes, not every secret, so a summary should still be written as a summary.
    /// </summary>
    public static partial class ChangeSanitizer
    {
        public const string Mask = "[redacted]";
        public const int MaxSummary = 500;
        public const int MaxValue = 256;
        public const int MaxAttributes = 16;

        public static readonly IReadOnlySet<string> AllowedAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "commit", "branch", "tag", "pipeline", "build", "image", "ticket", "url", "reason",
        };

        /// <summary>Masks secrets in text and bounds its length; empty text stays empty.</summary>
        public static string Text(string? value, int maxLength)
        {
            var text = (value ?? "").Trim();
            text = Control().Replace(text, " ");
            text = UrlCredentials().Replace(text, m => $"{m.Groups[1].Value}{Mask}@");
            text = Jwt().Replace(text, Mask);
            text = Bearer().Replace(text, m => $"{m.Groups[1].Value} {Mask}");
            text = KnownTokens().Replace(text, Mask);
            text = SecretAssignment().Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Mask}");
            text = SecretFlag().Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Mask}");
            return text.Length <= maxLength ? text : text[..maxLength];
        }

        /// <summary>The allowed attributes with their values masked. <paramref name="dropped"/> counts the rest, whose names aren't kept either.</summary>
        public static Dictionary<string, string> Attributes(IReadOnlyDictionary<string, string?>? attributes, out int dropped)
        {
            var kept = new Dictionary<string, string>();
            dropped = 0;
            foreach (var (key, value) in attributes ?? new Dictionary<string, string?>())
            {
                var text = Text(value, MaxValue);
                if (AllowedAttributes.Contains(key) && kept.Count < MaxAttributes && text.Length > 0) kept[key] = text;
                else dropped++;
            }
            return kept;
        }

        [GeneratedRegex(@"[\p{Cc}]+")]
        private static partial Regex Control();

        [GeneratedRegex(@"\b([a-z][a-z0-9+.-]*://)[^\s/@]+@", RegexOptions.IgnoreCase)]
        private static partial Regex UrlCredentials();

        [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}(\.[A-Za-z0-9_-]+)?")]
        private static partial Regex Jwt();

        [GeneratedRegex(@"\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{6,}", RegexOptions.IgnoreCase)]
        private static partial Regex Bearer();

        /// <summary>Prefixes of tokens from common services: GitHub, GitLab, Slack, AWS access keys, and private key headers.</summary>
        [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{10,}|github_pat_[A-Za-z0-9_]{10,}|glpat-[A-Za-z0-9_-]{10,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{12,})\b|-----BEGIN [A-Z ]*PRIVATE KEY-----")]
        private static partial Regex KnownTokens();

        /// <summary><c>password=x</c>, <c>"api_key": "x"</c>, or a connection string's <c>Pwd=x;</c>. A quoted value may hold spaces.</summary>
        [GeneratedRegex(@"([\w.-]*(?:password|passwd|pwd|secret|token|api[-_ ]?key|access[-_ ]?key|private[-_ ]?key|credential|authorization)[\w.-]*[""']?)(\s*[:=]\s*)(?:""[^""]*""|'[^']*'|[^\s;,&]+)", RegexOptions.IgnoreCase)]
        private static partial Regex SecretAssignment();

        /// <summary>A command line's <c>--password x</c>.</summary>
        [GeneratedRegex(@"(--?[\w-]*(?:password|passwd|pwd|secret|token|api[-_]?key)[\w-]*)(\s+)(?!-)(?:""[^""]*""|'[^']*'|\S+)", RegexOptions.IgnoreCase)]
        private static partial Regex SecretFlag();
    }
}
