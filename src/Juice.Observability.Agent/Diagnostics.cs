using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Agent
{
    /// <summary>
    /// The read-only diagnostics. Each reads the host through .NET's own APIs and, on Linux, files under <c>/proc</c>: none starts a process, runs a
    /// shell or loads anything named by a command. Each runs under a timeout and the caller's cancellation, and returns data bounded in size.
    /// </summary>
    /// <summary>An earlier read on this host is still stuck (a hung mount, say): this one isn't started, so they can't pile up.</summary>
    public sealed class DiagnosticBusyException() : Exception("busy: an earlier read on this host hasn't returned");

    /// <summary>Runs one typed diagnostic. An interface so a test can stand in a diagnostic that fails.</summary>
    public interface IDiagnostics
    {
        Task<JsonElement> RunAsync(object parameters, CancellationToken cancellationToken);
    }

    public sealed class Diagnostics(string hostRoot, string agentVersion) : IDiagnostics
    {
        public const int MaxFilesystems = 100;

        /// <summary>How long an action may take, beyond which it's cancelled and reported failed.</summary>
        public static TimeSpan Timeout(object parameters) => parameters is TcpReachabilityParameters tcp
            ? TimeSpan.FromMilliseconds(tcp.TimeoutMs) + TimeSpan.FromSeconds(1)
            : TimeSpan.FromSeconds(10);

        public async Task<JsonElement> RunAsync(object parameters, CancellationToken cancellationToken) => parameters switch
        {
            HostSummaryParameters => await BlockingAsync(() => JsonSerializer.SerializeToElement(HostSummary(), CommandSignature.Json), cancellationToken),
            DiskUsageParameters disk => await BlockingAsync(() => JsonSerializer.SerializeToElement(DiskUsage(disk), CommandSignature.Json), cancellationToken),
            TcpReachabilityParameters tcp => JsonSerializer.SerializeToElement(await TcpReachabilityAsync(tcp, cancellationToken), CommandSignature.Json),
            _ => throw new InvalidOperationException($"No diagnostic for {parameters.GetType().Name}"),
        };

        /// <summary>The blocking read still running after its command gave up on it, if any.</summary>
        private Task? _stuck;

        /// <summary>
        /// Runs a read that can't be cancelled (statfs on a hung network mount blocks in the kernel) on its own thread, and stops waiting when the
        /// command's time is up, so the agent goes on. While an abandoned read is still stuck, the next one is refused rather than stacked behind it.
        /// </summary>
        internal async Task<T> BlockingAsync<T>(Func<T> read, CancellationToken cancellationToken)
        {
            if (_stuck is { IsCompleted: false })
            {
                throw new DiagnosticBusyException();
            }
            var work = Task.Factory.StartNew(read, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                return await work.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _stuck = work;
                throw;
            }
        }

        public sealed record HostSummaryData(string Hostname, string OperatingSystem, string Architecture, long UptimeSeconds, int ProcessorCount,
            double[]? LoadAverage, long? MemoryTotalBytes, long? MemoryAvailableBytes, string AgentVersion);

        private HostSummaryData HostSummary()
        {
            var memory = ReadMemInfo();
            return new HostSummaryData(Dns.GetHostName(), RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(),
                Environment.TickCount64 / 1000, Environment.ProcessorCount, ReadLoadAverage(),
                memory.Total ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, memory.Available, agentVersion);
        }

        private double[]? ReadLoadAverage()
        {
            var file = Path.Combine(hostRoot, "proc", "loadavg");
            if (!OperatingSystem.IsLinux() || !File.Exists(file))
            {
                return null;
            }
            var parts = File.ReadAllText(file).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var values = parts.Take(3).Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null).ToList();
            return values.Count == 3 && values.All(v => v is not null) ? [.. values.Select(v => v!.Value)] : null;
        }

        private (long? Total, long? Available) ReadMemInfo()
        {
            var file = Path.Combine(hostRoot, "proc", "meminfo");
            if (!OperatingSystem.IsLinux() || !File.Exists(file))
            {
                return (null, null);
            }
            long? Read(string key) => File.ReadLines(file).FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal)) is { } line
                && long.TryParse(line[(key.Length + 1)..].Trim().Split(' ')[0], out var kb) ? kb * 1024 : null;
            return (Read("MemTotal"), Read("MemAvailable"));
        }

        public sealed record FilesystemData(string MountPoint, string Format, long TotalBytes, long FreeBytes, long AvailableBytes);

        public sealed record DiskUsageData(IReadOnlyList<FilesystemData> Filesystems, bool Truncated);

        private static readonly HashSet<string> PseudoFilesystems = ["proc", "sysfs", "devpts", "cgroup", "cgroup2", "mqueue", "debugfs", "tracefs",
            "securityfs", "pstore", "bpf", "configfs", "fusectl", "hugetlbfs", "autofs", "binfmt_misc", "nsfs", "rpc_pipefs", "devtmpfs"];

        private static DiskUsageData DiskUsage(DiskUsageParameters parameters)
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && !PseudoFilesystems.Contains(d.DriveFormat))
                .Select(d => new FilesystemData(d.RootDirectory.FullName, d.DriveFormat, d.TotalSize, d.TotalFreeSpace, d.AvailableFreeSpace));
            return Select(drives, parameters.PathPrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        /// <summary>
        /// What <c>disk.usage</c> reports for a path: the filesystems mounted at or under it, by their own mount point. When the path isn't a mount
        /// point itself, the filesystem it lives on is reported too, but named by the path asked for: its real mount point (often <c>/</c>) is outside
        /// what the local allowlist permitted, and isn't shown. With no path, every filesystem.
        /// </summary>
        internal static DiskUsageData Select(IEnumerable<FilesystemData> filesystems, string? path, StringComparison comparison)
        {
            static string Normalize(string p) => AgentPaths.Normalize(p);
            bool IsUnder(string a, string b) => AgentPaths.IsUnder(a, b, comparison);

            var all = filesystems.ToList();
            List<FilesystemData> chosen;
            if (path is null)
            {
                chosen = all;
            }
            else
            {
                var target = Normalize(path);
                chosen = [.. all.Where(f => IsUnder(Normalize(f.MountPoint), target))];
                if (!chosen.Any(f => Normalize(f.MountPoint).Equals(target, comparison))
                    && all.Where(f => IsUnder(target, Normalize(f.MountPoint))).MaxBy(f => Normalize(f.MountPoint).Length) is { } holding)
                {
                    chosen.Add(holding with { MountPoint = path });
                }
            }
            chosen = [.. chosen.OrderBy(f => f.MountPoint, StringComparer.Ordinal)];
            return new DiskUsageData([.. chosen.Take(MaxFilesystems)], chosen.Count > MaxFilesystems);
        }

        public sealed record TcpReachabilityData(string Host, int Port, bool Reachable, string? Address, long ElapsedMs, string? Error);

        private static async Task<TcpReachabilityData> TcpReachabilityAsync(TcpReachabilityParameters target, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(target.TimeoutMs);
            var started = Stopwatch.GetTimestamp();
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(target.Host, target.Port, timeout.Token);
                var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();
                return new TcpReachabilityData(target.Host, target.Port, true, address, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new TcpReachabilityData(target.Host, target.Port, false, null, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, "timeout");
            }
            catch (SocketException e)
            {
                return new TcpReachabilityData(target.Host, target.Port, false, null, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, e.SocketErrorCode.ToString());
            }
        }
    }
}
