using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Agent
{
    /// <param name="Name">The entry in the zip.</param>
    /// <param name="Source">The log file it came from, if it's a log excerpt.</param>
    /// <param name="Bytes">Its size, uncompressed.</param>
    /// <param name="Lines">Lines in it, for a log excerpt.</param>
    public sealed record BundleEntry(string Name, string? Source, long Bytes, int? Lines);

    /// <param name="BundleId">Where the platform keeps it, once uploaded.</param>
    /// <param name="SizeBytes">The zip's size.</param>
    /// <param name="Sha256">The zip's SHA-256, lowercase hex.</param>
    /// <param name="Entries">What's in it.</param>
    /// <param name="Skipped">Logs listed but left out, each with why: missing, a link, unreadable, or no room left.</param>
    /// <param name="Redacted">Values that looked like credentials, replaced with <c>[redacted]</c>.</param>
    public sealed record BundleSummary(Guid? BundleId, long SizeBytes, string Sha256, IReadOnlyList<BundleEntry> Entries, IReadOnlyList<string> Skipped, int Redacted);

    /// <summary>
    /// Makes a <c>diagnostics.bundle</c>: the host summary and disk usage the diagnostics read, each only as far as the allowlist lets those actions
    /// read (no <c>host.summary</c>, no summary; <c>disk.usage</c> within its <c>PathPrefixes</c>), the agent's version and allowlist (bounds only,
    /// never keys or tokens), and the last lines of each log file the allowlist lists. It reads through the agent's own <see cref="IDiagnostics"/>, so
    /// a read stuck on a hung mount refuses the next one instead of piling up. A log is read only by its exact listed path, never through a link,
    /// from at most <see cref="BundleLimits.MaxBytesPerLog"/> at its end. What looks like a credential (<c>password=…</c>, <c>Authorization: Bearer …</c>)
    /// is redacted. The whole stays under <see cref="BundleLimits.MaxBytes"/>: a log that doesn't fit is left out and said so.
    /// </summary>
    public sealed partial class DiagnosticBundle(IDiagnostics diagnostics, string agentVersion, LocalAllowlist allowlist, TimeProvider clock)
    {
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
        /// <summary>Kept free for the zip's own structure and the manifest.</summary>
        private const int Margin = 64 * 1024;

        public async Task<(byte[] Zip, BundleSummary Summary)> MakeAsync(DiagnosticsBundleParameters parameters, CancellationToken cancellationToken)
        {
            var entries = new List<(BundleEntry Entry, byte[] Content)>();
            var skipped = new List<string>();
            var redacted = 0;
            var budget = BundleLimits.MaxBytes - Margin;
            void Add(string name, string? source, byte[] content, int? lines)
            {
                entries.Add((new BundleEntry(name, source, content.Length, lines), content));
                budget -= content.Length;
            }
            byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(CommandSignature.Json) { WriteIndented = true });

            // What the host doesn't let its own diagnostics read, its bundle doesn't either.
            if (allowlist.Actions.ContainsKey(AgentActions.HostSummary))
                Add("host.json", null, Json(await diagnostics.RunAsync(new HostSummaryParameters(), cancellationToken)), null);
            else skipped.Add($"host summary: {AgentActions.HostSummary} isn't allowed on this host");
            if (allowlist.Actions.GetValueOrDefault(AgentActions.DiskUsage) is { } disk)
            {
                string?[] paths = disk.PathPrefixes.Count == 0 ? [null] : [.. disk.PathPrefixes];
                var usage = new List<JsonElement>();
                foreach (var path in paths) usage.Add(await diagnostics.RunAsync(new DiskUsageParameters(path), cancellationToken));
                Add("disk.json", null, Json(usage), null);
            }
            else skipped.Add($"disk usage: {AgentActions.DiskUsage} isn't allowed on this host");
            Add("agent.json", null, Json(new { version = agentVersion, madeAt = clock.GetUtcNow(), allowlist = allowlist.Actions }), null);

            var logs = allowlist.Actions.GetValueOrDefault(AgentActions.DiagnosticsBundle)?.LogFiles ?? [];
            var index = 0;
            foreach (var path in logs.Take(BundleLimits.MaxLogFiles))
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
                var file = new FileInfo(path);
                if (!file.Exists) { skipped.Add($"{path}: missing"); continue; }
                if (file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0) { skipped.Add($"{path}: a link, not followed"); continue; }
                string[] lines;
                try
                {
                    lines = Tail(file, parameters.Lines);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    skipped.Add($"{path}: unreadable ({e.GetType().Name})");
                    continue;
                }
                var text = new StringBuilder();
                foreach (var line in lines)
                {
                    var (clean, count) = Redact(line);
                    redacted += count;
                    text.Append(clean).Append('\n');
                }
                var content = Encoding.UTF8.GetBytes(text.ToString());
                if (content.Length > budget) { skipped.Add($"{path}: no room left in the bundle"); continue; }
                Add($"logs/{index:00}-{SafeName().Replace(file.Name, "_")}", path, content, lines.Length);
            }

            using var zip = new MemoryStream();
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (entry, content) in entries)
                {
                    await using var stream = archive.CreateEntry(entry.Name, CompressionLevel.Optimal).Open();
                    await stream.WriteAsync(content, cancellationToken);
                }
                var manifest = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var stream = manifest.Open())
                {
                    await stream.WriteAsync(Json(new { entries = entries.Select(e => e.Entry), skipped, redacted, logLines = parameters.Lines }), cancellationToken);
                }
            }
            var bytes = zip.ToArray();
            if (bytes.Length > BundleLimits.MaxBytes) throw new ActionFailedException("the bundle came out larger than its limit");
            return (bytes, new BundleSummary(null, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), [.. entries.Select(e => e.Entry)], skipped, redacted));
        }

        /// <summary>The last <paramref name="count"/> lines, from at most <see cref="BundleLimits.MaxBytesPerLog"/> at the end; a line cut by that start is dropped.</summary>
        internal static string[] Tail(FileInfo file, int count)
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - BundleLimits.MaxBytesPerLog);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            stream.ReadExactly(buffer);
            var lines = Encoding.UTF8.GetString(buffer).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var whole = start > 0 ? lines.Skip(1) : lines;
            var list = whole.ToList();
            if (list.Count > 0 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
            return [.. list.TakeLast(count)];
        }

        /// <summary>
        /// A value after a credential's name (<c>password=</c>, <c>token:</c>, <c>AccountKey=</c>, <c>Authorization: Bearer</c>), a bearer token on its
        /// own, and the password in a URL (<c>postgres://user:pass@host</c>) become <c>[redacted]</c>.
        /// </summary>
        internal static (string Line, int Count) Redact(string line)
        {
            var count = 0;
            var clean = Credential().Replace(line, m =>
            {
                count++;
                return m.Groups["name"].Value + m.Groups["sep"].Value + "[redacted]";
            });
            clean = UrlPassword().Replace(clean, m =>
            {
                count++;
                return m.Groups["head"].Value + "[redacted]@";
            });
            clean = Bearer().Replace(clean, m =>
            {
                count++;
                return m.Groups["head"].Value + "[redacted]";
            });
            return (clean, count);
        }

        [GeneratedRegex(@"(?<name>\b(?:password|passwd|pwd|secret|client_secret|token|access_token|refresh_token|api[_-]?key|apikey|authorization|accountkey|sharedaccesskey|sharedaccesssignature)\b""?)(?<sep>\s*[:=]\s*(?:bearer\s+|basic\s+)?)(?<value>""[^""]*""|'[^']*'|[^\s;,&""']+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Credential();

        /// <summary><c>scheme://user:password@</c>: the password part.</summary>
        [GeneratedRegex(@"(?<head>\b[a-z][a-z0-9+.-]*://[^\s:/@]+:)[^\s@/]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex UrlPassword();

        /// <summary>A bearer token anywhere, unless it's already redacted.</summary>
        [GeneratedRegex(@"(?<head>\bbearer\s+)(?!\[redacted\])[A-Za-z0-9\-._~+/]{8,}=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex Bearer();

        [GeneratedRegex(@"[^A-Za-z0-9._-]")]
        private static partial Regex SafeName();
    }
}
