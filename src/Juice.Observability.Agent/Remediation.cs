using System.Text.Json;
using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Agent
{
    /// <summary>
    /// The two remediations (roadmap Phase 8), within what the host's allowlist lists: the command has been verified and checked against it before
    /// this runs, and the cleanup reads its folder's own bounds from it again here. A failure is an <see cref="ActionFailedException"/> with a
    /// short reason, reported as the command's.
    /// </summary>
    public sealed class Remediation(LocalAllowlist allowlist, TimeProvider clock, IServiceControl? services = null)
    {
        private readonly IServiceControl _services = services ?? ServiceControl.ForThisHost();

        /// <summary>Files deleted per run at most; lower only in tests.</summary>
        internal int MaxFiles { get; init; } = FilesCleanupParameters.MaxFilesPerRun;

        public static bool Handles(object parameters) => parameters is ServiceRestartParameters or FilesCleanupParameters;

        /// <summary>How long it may take: a restart waits for the service, a cleanup for its folder.</summary>
        public static TimeSpan Timeout(object parameters) => parameters is ServiceRestartParameters ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(60);

        public async Task<JsonElement> RunAsync(object parameters, CancellationToken cancellationToken) => parameters switch
        {
            ServiceRestartParameters restart => JsonSerializer.SerializeToElement(await _services.RestartAsync(restart.Service, cancellationToken), CommandSignature.Json),
            FilesCleanupParameters cleanup => JsonSerializer.SerializeToElement(Clean(cleanup, cancellationToken), CommandSignature.Json),
            _ => throw new InvalidOperationException($"No remediation for {parameters.GetType().Name}"),
        };

        /// <param name="Folder">The folder cleaned.</param>
        /// <param name="DryRun">Nothing was deleted; the counts are what would have been.</param>
        /// <param name="KeepHours">The keep time used.</param>
        /// <param name="FilesRemoved">Files deleted, or that would be.</param>
        /// <param name="BytesRemoved">Their size.</param>
        /// <param name="FilesKept">Matching files written within the keep time.</param>
        /// <param name="OldestKept">When the oldest of those was written.</param>
        /// <param name="Failed">Files that couldn't be deleted (in use, no right): left as they were.</param>
        /// <param name="Limited">The run stopped at its limit; another run goes on.</param>
        public sealed record CleanupResult(string Folder, bool DryRun, int KeepHours, int FilesRemoved, long BytesRemoved, int FilesKept, DateTimeOffset? OldestKept,
            int Failed, bool Limited);

        /// <summary>
        /// Deletes regular files matching the folder's pattern, last written before the keep time, under the folder only: links are skipped and
        /// never followed, as are folders below unless the host says <c>Recursive</c>. Stops at <see cref="FilesCleanupParameters.MaxFilesPerRun"/>.
        /// </summary>
        internal CleanupResult Clean(FilesCleanupParameters cleanup, CancellationToken cancellationToken)
        {
            var folder = allowlist.Actions.GetValueOrDefault(AgentActions.FilesCleanup)?.FolderFor(cleanup)
                ?? throw new ActionFailedException("the folder isn't listed on this host");
            var root = Path.GetFullPath(folder.Path);
            if (!Directory.Exists(root)) throw new ActionFailedException("the folder doesn't exist");
            if (new DirectoryInfo(root).LinkTarget is not null) throw new ActionFailedException("the folder is a link");
            var keep = Math.Max(folder.KeepHours, cleanup.KeepHours ?? 0);
            var cutoff = clock.GetUtcNow() - TimeSpan.FromHours(keep);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = folder.Recursive, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true,
                MatchType = MatchType.Simple, ReturnSpecialDirectories = false,
            };
            int removed = 0, kept = 0, failed = 0;
            long bytes = 0;
            DateTimeOffset? oldestKept = null;
            var limited = false;
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            foreach (var path in Directory.EnumerateFiles(root, folder.Pattern, options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = new FileInfo(path);
                // Belt and braces: what enumeration returns is under the folder and not a link, or it's left alone.
                if (!Path.GetFullPath(path).StartsWith(prefix, AgentPaths.Comparison) || file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var written = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                if (written >= cutoff)
                {
                    kept++;
                    if (oldestKept is null || written < oldestKept) oldestKept = written;
                    continue;
                }
                if (removed == MaxFiles)
                {
                    limited = true;
                    break;
                }
                var size = file.Length;
                if (!cleanup.DryRun)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        failed++;
                        continue;
                    }
                }
                removed++;
                bytes += size;
            }
            return new CleanupResult(folder.Path, cleanup.DryRun, keep, removed, bytes, kept, oldestKept, failed, limited);
        }
    }

    /// <param name="Service">The service restarted.</param>
    /// <param name="State">What the service manager says it is after: <c>active</c> on systemd, <c>Running</c> on Windows.</param>
    /// <param name="Running">Whether that is running. The platform still waits for health evidence before calling anything recovered.</param>
    public sealed record ServiceRestartResult(string Service, string State, bool Running);

    /// <summary>Restarts a service through the host's service manager. An interface so tests can stand one in.</summary>
    public interface IServiceControl
    {
        Task<ServiceRestartResult> RestartAsync(string service, CancellationToken cancellationToken);
    }

    public static class ServiceControl
    {
        public static IServiceControl ForThisHost() => OperatingSystem.IsWindows() ? new WindowsServices() : new Systemd();
    }

    /// <summary>The Windows Service Control Manager: stop, wait, start, wait. No process is started.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public sealed class WindowsServices : IServiceControl
    {
        public async Task<ServiceRestartResult> RestartAsync(string service, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                using var controller = new System.ServiceProcess.ServiceController(service);
                try
                {
                    controller.Refresh();
                    if (controller.Status is not (System.ServiceProcess.ServiceControllerStatus.Stopped or System.ServiceProcess.ServiceControllerStatus.StopPending))
                    {
                        controller.Stop();
                    }
                    Wait(controller, System.ServiceProcess.ServiceControllerStatus.Stopped, cancellationToken);
                    controller.Start();
                    Wait(controller, System.ServiceProcess.ServiceControllerStatus.Running, cancellationToken);
                }
                catch (InvalidOperationException e)
                {
                    throw new ActionFailedException(e.InnerException?.Message ?? e.Message);
                }
                controller.Refresh();
                return new ServiceRestartResult(service, controller.Status.ToString(), controller.Status == System.ServiceProcess.ServiceControllerStatus.Running);
            }, cancellationToken);
        }

        private static void Wait(System.ServiceProcess.ServiceController controller, System.ServiceProcess.ServiceControllerStatus status, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    controller.WaitForStatus(status, TimeSpan.FromSeconds(1));
                    return;
                }
                catch (System.ServiceProcess.TimeoutException)
                {
                }
            }
        }
    }
}
