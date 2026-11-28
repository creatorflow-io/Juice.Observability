using System.Text.RegularExpressions;

namespace Juice.Observability.Api.Integrations
{
    /// <summary>
    /// A secret reference: <c>env:NAME</c> with NAME starting with the configured prefix, or <c>file:/path</c> inside the one configured folder. Checked when it is
    /// saved and again when it is resolved (W-24). A reference is a name, never the value.
    /// </summary>
    internal static partial class SecretReference
    {
        public const int MaxLength = 512, MaxValueLength = 8192;

        [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
        private static partial Regex EnvName();

        private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static bool IsAllowedFromApi(string? reference, string envPrefix, string? directory) => Error(reference, envPrefix, directory) is null;

        /// <summary>Null when the reference is allowed; otherwise why not (never echoing more than the reference itself).</summary>
        public static string? Error(string? reference, string? envPrefix, string? directory)
        {
            if (string.IsNullOrEmpty(reference) || reference.Length > MaxLength || reference.Any(char.IsControl))
            {
                return "a secret reference is env:NAME or file:/path, at most 512 characters";
            }
            if (reference.StartsWith("env:", StringComparison.Ordinal))
            {
                var name = reference[4..];
                // An empty prefix would allow every variable of the process, the database password among them.
                if (string.IsNullOrEmpty(envPrefix) || !EnvName().IsMatch(name) || !name.StartsWith(envPrefix, StringComparison.Ordinal) || name.Length == envPrefix.Length)
                {
                    return $"an env reference names a variable starting with '{envPrefix}'";
                }
                return null;
            }
            if (reference.StartsWith("file:", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return "file references are not enabled on this deployment";
                }
                return InsideDirectory(reference[5..], directory) is null ? "a file reference is an absolute path inside the configured secrets folder" : null;
            }
            return "a secret reference is env:NAME or file:/path";
        }

        /// <summary>The full path when <paramref name="path"/> is absolute, has no <c>..</c> segment and is inside <paramref name="directory"/>; otherwise null.</summary>
        private static string? InsideDirectory(string path, string directory)
        {
            if (path.Length == 0 || path.Contains('\0') || !Path.IsPathFullyQualified(path) || path.Split('/', '\\').Contains(".."))
            {
                return null;
            }
            var root = Path.GetFullPath(directory);
            if (!root.EndsWith(Path.DirectorySeparatorChar))
            {
                root += Path.DirectorySeparatorChar;
            }
            var full = Path.GetFullPath(path);
            return full.StartsWith(root, PathComparison) && full.Length > root.Length ? full : null;
        }

        /// <summary>The value of the reference, or null with the reason. The reference is checked again here: configuration may have changed since it was saved.</summary>
        public static async ValueTask<(string? Value, string? Reason)> ResolveAsync(string reference, string? envPrefix, string? directory, CancellationToken ct)
        {
            if (Error(reference, envPrefix, directory) is not null)
            {
                return (null, "the reference is not allowed on this deployment");
            }
            string? value;
            if (reference.StartsWith("env:", StringComparison.Ordinal))
            {
                value = Environment.GetEnvironmentVariable(reference[4..]);
                if (value is null)
                {
                    return (null, "the environment variable is not set");
                }
            }
            else
            {
                var full = InsideDirectory(reference[5..], directory!)!;
                try
                {
                    // A link out of the folder is no more allowed than a path out of it.
                    var info = new FileInfo(full);
                    if (info.ResolveLinkTarget(true) is { } target && InsideDirectory(target.FullName, directory!) is null)
                    {
                        return (null, "the file is outside the secrets folder");
                    }
                    if (!info.Exists || info.Length > MaxValueLength)
                    {
                        return (null, info.Exists ? "the file is too large" : "the file does not exist");
                    }
                    value = (await File.ReadAllTextAsync(full, ct)).TrimEnd('\r', '\n');
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return (null, "the file can't be read");
                }
            }
            return value.Length == 0 ? (null, "the secret is empty") : value.Length > MaxValueLength ? (null, "the secret is too large") : (value, null);
        }
    }

    /// <summary>
    /// The secrets of one instance's validation or run. Only declared names resolve, and only now, when the plugin asks. Every value it hands out is kept in
    /// <see cref="Values"/> so that what the plugin returns or logs can be scrubbed of it.
    /// </summary>
    internal sealed class InstanceSecrets(IReadOnlyDictionary<string, string> references, IReadOnlySet<string> declared, IntegrationsOptions options) : IIntegrationSecrets
    {
        private readonly List<string> _values = [];

        public IReadOnlyCollection<string> Values
        {
            get
            {
                lock (_values)
                {
                    return [.. _values];
                }
            }
        }

        public async ValueTask<string> GetAsync(string name, CancellationToken ct)
        {
            if (!declared.Contains(name))
            {
                throw new IntegrationSecretException(name, "not a declared secret");
            }
            if (!references.TryGetValue(name, out var reference))
            {
                throw new IntegrationSecretException(name, "no reference is set");
            }
            var (value, reason) = await SecretReference.ResolveAsync(reference, options.SecretEnvPrefix, options.SecretDirectory, ct);
            if (value is null)
            {
                throw new IntegrationSecretException(name, reason ?? "unavailable");
            }
            lock (_values)
            {
                _values.Add(value);
            }
            return value;
        }
    }
}
