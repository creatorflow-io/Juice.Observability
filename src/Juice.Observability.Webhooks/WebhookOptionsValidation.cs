using Microsoft.Extensions.Options;

namespace Juice.Observability.Webhooks
{
    /// <summary>
    /// Checks <see cref="WebhookOptions"/> at start (M53). <c>Webhooks:ConsoleUrl</c>, when set, is the base of the links in notifications, in
    /// legacy and in the new pipeline alike: an absolute <c>http</c> or <c>https</c> URL with no userinfo, query or fragment, and no whitespace or
    /// control character. It is not normalised: an accepted value builds the same link on both sides. A message never repeats the value.
    /// </summary>
    public sealed class WebhookOptionsValidation : IValidateOptions<WebhookOptions>
    {
        public const string ConsoleUrlKey = "Webhooks:ConsoleUrl";
        public const int MaxConsoleUrlLength = 2048;

        public ValidateOptionsResult Validate(string? name, WebhookOptions options)
            => ConsoleUrlProblem(options.ConsoleUrl) is { } problem
                ? ValidateOptionsResult.Fail($"{ConsoleUrlKey} {problem}")
                : ValidateOptionsResult.Success;

        /// <summary>Why the value is refused (without quoting it), or null when it is valid.</summary>
        internal static string? ConsoleUrlProblem(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            if (value.Length > MaxConsoleUrlLength)
            {
                return $"must be at most {MaxConsoleUrlLength} characters.";
            }
            if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                return "must not contain whitespace or control characters.";
            }
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || !value.StartsWith(uri.Scheme + "://", StringComparison.OrdinalIgnoreCase))
            {
                return "must be an absolute http or https URL (such as https://host.example.com).";
            }
            var authority = value[(uri.Scheme.Length + 3)..];
            var end = authority.IndexOfAny(['/', '?', '#']);
            if (uri.UserInfo.Length > 0 || (end < 0 ? authority : authority[..end]).Contains('@'))
            {
                return "must not contain user information.";
            }
            if (value.Contains('?') || value.Contains('#'))
            {
                return "must not contain a query or a fragment.";
            }
            return null;
        }
    }
}
