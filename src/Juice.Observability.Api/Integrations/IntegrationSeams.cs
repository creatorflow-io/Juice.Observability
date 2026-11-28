using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Juice.Observability.Api.Integrations
{
    /// <summary>Canonical JSON: object properties in ordinal order, no whitespace. What is stored and hashed, so equal configuration is equal text.</summary>
    internal static class IntegrationJson
    {
        public static string Canonical(JsonElement value)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                Write(writer, value);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public static string Canonical(string json)
        {
            using var document = JsonDocument.Parse(json);
            return Canonical(document.RootElement);
        }

        private static void Write(Utf8JsonWriter writer, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        Write(writer, property.Value);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in value.EnumerateArray())
                    {
                        Write(writer, item);
                    }
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.Number when value.TryGetDecimal(out var number):
                    // The database's jsonb rewrites numbers (1.0, 1e2): one spelling per value keeps a stored text equal to the one that was hashed.
                    writer.WriteNumberValue(number / 1.0000000000000000000000000000m);
                    break;
                default:
                    value.WriteTo(writer);
                    break;
            }
        }
    }

    /// <summary>What a validation vouches for: the plugin, its config version, the config, the scope and the secret references. Changing any of them invalidates it.</summary>
    internal static class IntegrationHash
    {
        public static string Of(IntegrationInstance instance)
            => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('\n', instance.PluginKey, instance.ConfigVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    IntegrationJson.Canonical(instance.Config), IntegrationJson.Canonical(instance.Scope), IntegrationJson.Canonical(instance.SecretRefs)))));
    }

    /// <summary>
    /// Removes resolved secret values, in the forms they usually appear in (as is, URL-encoded, JSON-escaped, base64), from text before it is stored or logged.
    /// <para>
    /// Its limits: whole values only (a value the plugin splits, hashes or otherwise transforms is not found), and only what goes through the core (the
    /// context's <c>Logger</c>, outcomes, validation reports). A plugin that injects its own <c>ILogger&lt;T&gt;</c> bypasses it. Plugins are trusted, compiled-in
    /// code (W-15); the scrubber guards against accidents, not against a hostile plugin.
    /// </para>
    /// </summary>
    internal static class IntegrationScrubber
    {
        public const string Mask = "***";

        /// <summary>The longest plugin text kept: detail, check detail and failure reasons.</summary>
        public const int MaxText = 500;

        public static string? Scrub(string? text, IEnumerable<string> secretValues)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            var forms = secretValues.Where(v => !string.IsNullOrEmpty(v)).SelectMany(Forms).Distinct(StringComparer.Ordinal).OrderByDescending(f => f.Length);
            foreach (var form in forms)
            {
                text = text.Replace(form, Mask, StringComparison.Ordinal);
            }
            return text;
        }

        /// <summary>Scrubbed, then capped: scrubbing the whole text first means a cut can't leave the front of a value behind.</summary>
        public static string? Clean(string? text, IEnumerable<string> secretValues, int max = MaxText)
        {
            var scrubbed = Scrub(text, secretValues);
            return scrubbed is { } s && s.Length > max ? string.Concat(s.AsSpan(0, max - 1), "…") : scrubbed;
        }

        private static IEnumerable<string> Forms(string value)
        {
            yield return value;
            yield return Uri.EscapeDataString(value);
            yield return JsonEncodedText.Encode(value).ToString();
            yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }
    }

    /// <summary>
    /// The checkpoints of one run: reads fall through to the stored ones, and what the plugin sets is buffered in <see cref="Pending"/> until the run's
    /// outcome is accepted (then written with it) or dropped.
    /// </summary>
    internal sealed partial class RunCheckpoints(Func<string, CancellationToken, ValueTask<IntegrationCheckpoint?>> load, bool readOnly = false) : IIntegrationCheckpoints
    {
        public const int MaxStreams = 32, MaxCursor = 1024;

        private readonly Dictionary<string, IntegrationCheckpoint> _pending = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        [GeneratedRegex("^[a-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
        private static partial Regex StreamName();

        /// <summary>What the plugin set during the run, by stream.</summary>
        public IReadOnlyDictionary<string, IntegrationCheckpoint> Pending
        {
            get
            {
                lock (_gate)
                {
                    return new Dictionary<string, IntegrationCheckpoint>(_pending, StringComparer.Ordinal);
                }
            }
        }

        public async ValueTask<IntegrationCheckpoint?> GetAsync(string stream, CancellationToken ct)
        {
            lock (_gate)
            {
                if (_pending.TryGetValue(stream, out var set))
                {
                    return set;
                }
            }
            return await load(stream, ct);
        }

        public void Set(string stream, DateTimeOffset watermark, string? cursor = null)
        {
            if (readOnly)
            {
                throw new InvalidOperationException("Checkpoints can't be set during a validation.");
            }
            if (stream is null || !StreamName().IsMatch(stream))
            {
                throw new ArgumentException("A checkpoint stream is 1 to 64 of [a-z0-9._-].", nameof(stream));
            }
            if (cursor is { Length: > MaxCursor })
            {
                throw new ArgumentException($"A checkpoint cursor is at most {MaxCursor} characters.", nameof(cursor));
            }
            lock (_gate)
            {
                if (!_pending.ContainsKey(stream) && _pending.Count >= MaxStreams)
                {
                    throw new InvalidOperationException($"A run keeps at most {MaxStreams} checkpoint streams.");
                }
                _pending[stream] = new IntegrationCheckpoint(stream, watermark, cursor);
            }
        }
    }
}
