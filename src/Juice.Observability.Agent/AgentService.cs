using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Juice.Observability.Agent.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Agent
{
    /// <summary>
    /// The agent's life: enroll once, then poll the platform for commands, check each one locally, run what passes, and report every outcome,
    /// refusals included. Renews its certificate before it expires. Never listens on a port, and never stops on a failure: it logs it and tries again,
    /// later each time, so one bad answer or a platform outage doesn't take the agent down.
    /// </summary>
    public sealed class AgentService(IOptions<AgentOptions> options, IOptions<LocalAllowlist> allowlist, TimeProvider clock, ILogger<AgentService> logger,
        IHostApplicationLifetime? lifetime = null, AgentActionCatalog? actions = null) : BackgroundService
    {
        /// <summary>
        /// The version as packages name it: the build metadata after <c>+</c> left out. The program's own version (<see cref="JuiceAgent.RunAsync"/>
        /// sets it), so a customer's build with custom actions is known by its own release, as its packages are.
        /// </summary>
        public static string Version { get; internal set; } = VersionOf(typeof(AgentService).Assembly);

        internal static string VersionOf(Assembly assembly)
            => (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

        private AgentActionCatalog Actions => actions ?? AgentActionCatalog.BuiltIn;

        /// <summary>The exit code when the configuration is invalid (EX_CONFIG): nothing a restart alone fixes.</summary>
        public const int ConfigurationExitCode = 78;

        /// <summary>How long a download, check and unpack may take.</summary>
        internal static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(5);

        private AgentUpdater? _updater;

        internal AgentUpdater Updater
        {
            get => _updater ??= AgentUpdater.Create(options.Value);
            set => _updater = value;
        }

        /// <summary>An update is unpacked and asked for: the agent stops for the launcher to start the new version.</summary>
        internal bool RestartRequested { get; private set; }

        /// <summary>Renew when less than this share of the certificate's life is left.</summary>
        private const double RenewAt = 1.0 / 3;
        /// <summary>Results kept for sending when the platform can't take them: beyond this the oldest go, and the platform hands those commands over again.</summary>
        internal const int MaxUnsent = 100;
        internal const string Interrupted = "interrupted";

        private AgentStore Store => new(options.Value.DataDirectory);

        /// <summary>The connection and the certificate it uses, kept until the certificate is renewed or the platform refuses it, then disposed together.</summary>
        private sealed class Connection(PlatformClient client, X509Certificate2 certificate) : IDisposable
        {
            public PlatformClient Client { get; } = client;
            public X509Certificate2 Certificate { get; } = certificate;

            public void Dispose()
            {
                Client.Dispose();
                Certificate.Dispose();
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var errors = options.Value.Validate().Concat(allowlist.Value.Validate()).ToList();
            if (errors.Count > 0)
            {
                // A failed background service stops the host with exit code 0 unless told otherwise; a service manager must see a failure.
                Environment.ExitCode = ConfigurationExitCode;
                throw new InvalidOperationException("Invalid agent configuration:\n" + string.Join("\n", errors));
            }
            if (allowlist.Value.Actions.Count == 0)
            {
                logger.LogWarning("The local allowlist permits no action: every command will be refused. Set Allowlist:Actions.");
            }
            if (allowlist.Value.Actions.ContainsKey(AgentActions.AgentUpdate))
            {
                if (!Updater.UnderLauncher) logger.LogWarning("The allowlist permits {Action}, but the agent wasn't started by its launcher: updates will fail.", AgentActions.AgentUpdate);
                else if (options.Value.ReleaseKeyFiles.Count == 0) logger.LogWarning("The allowlist permits {Action}, but no Agent:ReleaseKeyFiles are set: updates will fail.", AgentActions.AgentUpdate);
            }
            var diagnostics = new Diagnostics(options.Value.HostRoot, Version);
            var unsent = new Queue<CommandResult>();
            var actionsReported = false;
            // After a refusal: the token known not to help, until another appears.
            string? waitingFor = null;
            AgentIdentity? identity = null;
            CommandMemory? memory = null;
            Connection? connection = null;
            var delay = TimeSpan.FromSeconds(1);
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        if (identity is null)
                        {
                            await EnrollIfNeededAsync(stoppingToken);
                            // Both or neither: a pass that fails here leaves the agent to start again on the next one.
                            var loaded = Store.LoadIdentity();
                            memory = CommandMemory.Open(Store.MemoryFile, out var setAside);
                            if (setAside is not null)
                            {
                                logger.LogError("The command memory didn't read: moved to {File}, starting with an empty one", setAside);
                            }
                            identity = loaded;
                            if (Store.UsedTokenHash is null && await TokenAsync(stoppingToken) is { } present)
                            {
                                // Enrolled before tokens were recorded: the token there now is taken to be the one it used, so only a new one re-enrolls.
                                Store.RecordToken(present);
                            }
                            logger.LogInformation("Agent {AgentId} of {Tenant} started, version {Version}", identity.AgentId, identity.Tenant, Version);
                        }
                        if (Updater.ReadOutcome() is { } outcome)
                        {
                            // An update this host asked for ended, here or in the version before: its result goes to the platform now.
                            var reported = AgentUpdater.Result(outcome);
                            memory!.Complete(outcome.CommandId, reported);
                            Keep(unsent, reported);
                            Updater.ClearOutcome();
                            logger.Log(outcome.Succeeded ? LogLevel.Information : LogLevel.Error, "Update from {From} to {To}: {Outcome}",
                                outcome.From, outcome.To, outcome.Succeeded ? "succeeded" : $"rolled back ({outcome.Reason})");
                        }
                        connection ??= Connect();
                        if (await RenewIfDueAsync(connection, stoppingToken))
                        {
                            connection.Dispose();
                            connection = Connect();
                        }
                        await SendAsync(connection.Client, unsent, stoppingToken);
                        actionsReported = actionsReported || await ReportActionsAsync(connection.Client, stoppingToken);
                        var poll = await connection.Client.PollAsync(options.Value.PollWaitSeconds, Version, Dns.GetHostName(), stoppingToken, identity.CommandKeys.Keys);
                        // It reached the platform with its certificate: a version on trial has proved itself.
                        Updater.Confirm();
                        identity = TrustAnnounced(poll.Keys, identity);
                        var client = connection.Client;
                        foreach (var command in poll.Commands)
                        {
                            if (await HandleAsync(command, identity, memory!, diagnostics, stoppingToken, client.DownloadAsync, client.UploadBundleAsync) is { } result)
                            {
                                Keep(unsent, result);
                            }
                            if (RestartRequested)
                            {
                                break;   // the rest come again to the new version
                            }
                        }
                        await SendAsync(connection.Client, unsent, stoppingToken);
                        if (RestartRequested)
                        {
                            logger.LogInformation("Stopping for the launcher to start the new version");
                            Environment.ExitCode = AgentHome.UpdateExitCode;
                            lifetime?.StopApplication();
                            return;
                        }
                        delay = TimeSpan.FromSeconds(1);
                        continue;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (AgentRefusedException e)
                    {
                        // Revoked, expired or no longer known, or an enrollment token that isn't valid: only a person can fix it, with a new token.
                        var token = await TokenAsync(stoppingToken);
                        if (identity is not null && token is not null && AgentStore.HashToken(token) != Store.UsedTokenHash)
                        {
                            // An admin's new token is here: this identity is done, and the agent enrolls again with a new key. The old files are kept aside.
                            var aside = Store.SetAside(identity.AgentId, clock.GetUtcNow());
                            logger.LogWarning("The platform refused agent {AgentId} ({Reason}), and a new enrollment token is here: enrolling again. The old identity is in {Folder}.",
                                identity.AgentId, e.Message, aside);
                            (identity, memory, actionsReported, waitingFor) = (null, null, false, null);
                            unsent.Clear();
                            delay = TimeSpan.FromSeconds(1);
                        }
                        else
                        {
                            logger.LogError("The platform refused this agent ({Reason}). An admin's new enrollment token in its token file brings it back.", e.Message);
                            // Slowly, but at once when a token other than this one appears.
                            waitingFor = token is null ? Store.UsedTokenHash ?? "" : AgentStore.HashToken(token);
                            delay = TimeSpan.FromMinutes(5);
                        }
                    }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
                    {
                        logger.LogWarning("The platform is unreachable: {Error}. Retrying in {Delay}.", e.Message, delay);
                        delay = Longer(delay);
                    }
                    catch (Exception e)
                    {
                        // Anything else (an unreadable answer, a key that won't load): the agent stays up and tries again, and the log says what happened.
                        logger.LogError(e, "Agent pass failed. Retrying in {Delay}.", delay);
                        delay = Longer(delay);
                    }
                    connection?.Dispose();
                    connection = null;
                    if (waitingFor is not null)
                    {
                        await WaitForTokenAsync(waitingFor, delay, stoppingToken);
                        waitingFor = null;
                        continue;
                    }
                    await Task.Delay(delay, clock, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
                }
            }
            finally
            {
                connection?.Dispose();
            }
        }

        /// <summary>Adds the first announced key that a trusted key signed; ignores the rest. Returns the identity to use from now on.</summary>
        internal AgentIdentity TrustAnnounced(IReadOnlyList<SignedKey>? announcements, AgentIdentity identity)
        {
            foreach (var announcement in announcements ?? [])
            {
                if (identity.CommandKeys.ContainsKey(announcement.KeyId))
                {
                    continue;
                }
                using var key = CommandVerifier.VerifyAnnouncement(announcement, identity);
                if (key is null)
                {
                    logger.LogWarning("Ignored an announcement of command key {KeyId}: it isn't signed by a key this agent trusts", announcement.KeyId);
                    continue;
                }
                Store.TrustKey(announcement.SignedBy, identity.CommandKeys[announcement.SignedBy], announcement.KeyId, key);
                logger.LogInformation("Now trusting command key {KeyId}, announced by {SignedBy}", announcement.KeyId, announcement.SignedBy);
                return Store.LoadIdentity();
            }
            return identity;
        }

        /// <summary>
        /// What this agent can run and what this host permits, for the platform to offer (<see cref="LocalAllowlist.Report"/>). Says when an update
        /// can't work here, so nobody has to find out from a failed command.
        /// </summary>
        internal ActionsReport Report()
        {
            var update = new List<string>();
            if (!Updater.UnderLauncher) update.Add("won't work here: the agent wasn't started by its launcher");
            if (options.Value.ReleaseKeyFiles.Count == 0) update.Add("won't work here: no release key is trusted (Agent:ReleaseKeyFiles)");
            return allowlist.Value.Report(Actions, new Dictionary<string, IReadOnlyList<string>> { [AgentActions.AgentUpdate] = update });
        }

        /// <summary>
        /// Sends the report once per start. True when it's done: sent, or the platform is older and has nowhere to take it. A failure is logged and
        /// tried again on the next pass; it never holds up commands.
        /// </summary>
        internal async Task<bool> ReportActionsAsync(PlatformClient client, CancellationToken cancellationToken)
        {
            try
            {
                if (!await client.ReportActionsAsync(Report(), cancellationToken))
                {
                    logger.LogInformation("The platform doesn't take action reports (an older version): the Console will show the built-in actions only");
                }
                return true;
            }
            catch (ResultRefusedException e)
            {
                logger.LogError("The platform refused this agent's action report: {Detail}. Check the custom actions in this build.", e.Message);
                return true;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Couldn't report this agent's actions: {Error}. Trying again on the next pass.", e.Message);
                return false;
            }
        }

        /// <summary>How often a refused agent looks for a new enrollment token while it waits.</summary>
        internal TimeSpan TokenCheck { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Makes the bundle, uploads it, and answers with what's in it and where the platform keeps it.</summary>
        private async Task<System.Text.Json.JsonElement> BundleAsync(Guid commandId, DiagnosticsBundleParameters parameters, IDiagnostics diagnostics,
            Func<Guid, byte[], CancellationToken, Task<Guid>>? upload, CancellationToken cancellationToken)
        {
            if (upload is null) throw new ActionFailedException("no platform to upload the bundle to");
            var made = await new DiagnosticBundle(diagnostics, Version, allowlist.Value, clock).MakeAsync(parameters, cancellationToken);
            Guid id;
            try
            {
                id = await upload(commandId, made.Zip, cancellationToken);
            }
            catch (ResultRefusedException e)
            {
                throw new ActionFailedException($"the platform refused the bundle: {e.Message}");
            }
            return System.Text.Json.JsonSerializer.SerializeToElement(made.Summary with { BundleId = id }, CommandSignature.Json);
        }

        /// <summary>Runs <c>service.restart</c> and <c>files.cleanup</c>; a test may stand in its own service control.</summary>
        internal Remediation Remedies
        {
            get => _remedies ??= new Remediation(allowlist.Value, clock);
            set => _remedies = value;
        }
        private Remediation? _remedies;

        /// <summary>The enrollment token, from its file or the setting; null when there's none.</summary>
        private async Task<string?> TokenAsync(CancellationToken cancellationToken)
        {
            if (options.Value.EnrollmentTokenFile is { Length: > 0 } file)
            {
                try
                {
                    return (await File.ReadAllTextAsync(file, cancellationToken)).Trim() is { Length: > 0 } token ? token : null;
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
                {
                    return null;
                }
            }
            return string.IsNullOrWhiteSpace(options.Value.EnrollmentToken) ? null : options.Value.EnrollmentToken.Trim();
        }

        /// <summary>Waits up to <paramref name="delay"/>, ending early when a token other than <paramref name="refused"/> appears.</summary>
        private async Task WaitForTokenAsync(string refused, TimeSpan delay, CancellationToken cancellationToken)
        {
            var until = clock.GetUtcNow() + delay;
            while (!cancellationToken.IsCancellationRequested && clock.GetUtcNow() < until)
            {
                await Task.Delay(TokenCheck, clock, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
                if (await TokenAsync(cancellationToken) is { } token && AgentStore.HashToken(token) != refused) return;
            }
        }

        private static TimeSpan Longer(TimeSpan delay) => TimeSpan.FromSeconds(Math.Min(Math.Max(delay.TotalSeconds, 1) * 2, 60));

        private Connection Connect()
        {
            var certificate = Store.LoadCertificate();
            return new Connection(new PlatformClient(options.Value, certificate), certificate);
        }

        /// <summary>A result to send, keeping at most <see cref="MaxUnsent"/>: the platform hands any command it has no answer for over again.</summary>
        internal void Keep(Queue<CommandResult> unsent, CommandResult result)
        {
            unsent.Enqueue(result);
            while (unsent.Count > MaxUnsent)
            {
                var dropped = unsent.Dequeue();
                logger.LogWarning("Too many results waiting to be sent: dropped the one for command {CommandId}", dropped.CommandId);
            }
        }

        /// <summary>
        /// Sends what's waiting, oldest first. A result the platform refuses for good (a command it doesn't know for this agent, a body it won't take)
        /// is dropped, so it can't block the ones behind it or the next poll. Network failures leave it for the next pass.
        /// </summary>
        internal async Task SendAsync(PlatformClient client, Queue<CommandResult> unsent, CancellationToken cancellationToken)
        {
            while (unsent.TryPeek(out var result))
            {
                try
                {
                    await client.ReportAsync(result, cancellationToken);
                }
                catch (ResultRefusedException e)
                {
                    logger.LogWarning("The platform won't take the result for command {CommandId} ({Reason}): dropped", result.CommandId, e.Message);
                }
                unsent.Dequeue();
            }
        }

        /// <summary>One command, end to end. Returns what to report, or null when it can't even be attributed to a command.</summary>
        internal async Task<CommandResult?> HandleAsync(SignedCommand command, AgentIdentity identity, CommandMemory memory, IDiagnostics diagnostics, CancellationToken cancellationToken,
            Func<string, CancellationToken, Task<(byte[] Package, byte[] Signature)>>? download = null,
            Func<Guid, byte[], CancellationToken, Task<Guid>>? upload = null)
        {
            var now = clock.GetUtcNow();
            var verification = CommandVerifier.Verify(command, identity, allowlist.Value, now, Actions);
            if (verification.Command is not { } verified)
            {
                logger.LogWarning("Refused command {CommandId}: {Reason}", verification.CommandId, verification.Rejection);
                return verification.CommandId is { } id ? new CommandResult(id, CommandOutcome.Rejected, verification.Rejection, null, 0) : null;
            }
            var envelope = verified.Envelope;
            var (recall, earlier) = memory.Recall(envelope, now);
            switch (recall)
            {
                case Recall.Replayed:
                    logger.LogWarning("Refused command {CommandId}: its nonce was used by another command", envelope.CommandId);
                    return new CommandResult(envelope.CommandId, CommandOutcome.Rejected, RejectionReasons.Replayed, null, 0);
                case Recall.Redelivered or Recall.AlreadyDone when earlier is not null:
                    return earlier with { CommandId = envelope.CommandId };
                case Recall.Redelivered when envelope.CommandId == Updater.Trial:
                    // The update that started this version, not finished: it's reported once the launcher keeps this version or goes back.
                    return null;
                case Recall.Redelivered:
                    // Started before and never finished: the agent stopped while running it. Not run again, so a command that brings the agent down
                    // can't do it on every restart.
                    logger.LogWarning("Command {CommandId} was interrupted earlier; not running it again", envelope.CommandId);
                    var interrupted = new CommandResult(envelope.CommandId, CommandOutcome.Failed, Interrupted, null, 0);
                    memory.Complete(envelope, interrupted);
                    return interrupted;
            }

            memory.Record(envelope);
            var started = Stopwatch.GetTimestamp();
            if (verified.Parameters is AgentUpdateParameters update)
            {
                if (await UpdateAsync(envelope, update, download, started, cancellationToken) is not { } updated)
                {
                    return null;   // the new version or, after a rollback, this one reports it
                }
                memory.Complete(envelope, updated);
                logger.LogInformation("Command {CommandId} {Action}: {Outcome}", envelope.CommandId, envelope.Action, updated.Outcome);
                return updated;
            }
            CommandResult result;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var custom = verified.Parameters as CustomInvocation;
                var remedial = Remediation.Handles(verified.Parameters);
                var bundle = verified.Parameters as DiagnosticsBundleParameters;
                timeout.CancelAfter(custom?.Action.Timeout ?? (remedial ? Remediation.Timeout(verified.Parameters) : bundle is not null ? DiagnosticBundle.Timeout : Diagnostics.Timeout(verified.Parameters)));
                try
                {
                    // A custom action's own code: the agent stops waiting at its timeout even if it ignores the cancellation.
                    var data = custom is not null
                        ? System.Text.Json.JsonSerializer.SerializeToElement(await custom.Action.RunAsync(custom.Parameters, timeout.Token).WaitAsync(timeout.Token), CommandSignature.Json)
                        : bundle is not null
                            ? await BundleAsync(envelope.CommandId, bundle, diagnostics, upload, timeout.Token)
                        : remedial
                            ? await Remedies.RunAsync(verified.Parameters, timeout.Token)
                            : await diagnostics.RunAsync(verified.Parameters, timeout.Token);
                    result = new CommandResult(envelope.CommandId, CommandOutcome.Succeeded, null, data, Elapsed(started));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    result = new CommandResult(envelope.CommandId, CommandOutcome.Failed, "timeout", null, Elapsed(started));
                }
                catch (ActionFailedException e)
                {
                    var reason = e.Message.Length > 100 ? e.Message[..100] : e.Message;
                    logger.LogWarning("Command {CommandId} {Action} failed: {Reason}", envelope.CommandId, envelope.Action, reason);
                    result = new CommandResult(envelope.CommandId, CommandOutcome.Failed, reason, null, Elapsed(started));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Whatever a diagnostic throws is its failure, reported as such: never the agent's.
                    logger.LogWarning(e, "Command {CommandId} {Action} failed", envelope.CommandId, envelope.Action);
                    result = new CommandResult(envelope.CommandId, CommandOutcome.Failed, e is DiagnosticBusyException ? "busy" : e.GetType().Name, null, Elapsed(started));
                }
            }
            memory.Complete(envelope, result);
            logger.LogInformation("Command {CommandId} {Action}: {Outcome} in {DurationMs} ms", envelope.CommandId, envelope.Action, result.Outcome, result.DurationMs);
            return result;
        }

        /// <summary>
        /// The result of an update that can't go ahead, or of one that has nothing to do; null when it's unpacked and the agent must stop for the
        /// launcher (<see cref="RestartRequested"/>).
        /// </summary>
        private async Task<CommandResult?> UpdateAsync(CommandEnvelope envelope, AgentUpdateParameters update,
            Func<string, CancellationToken, Task<(byte[] Package, byte[] Signature)>>? download, long started, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(UpdateTimeout);
            try
            {
                var fetch = download ?? ((_, _) => throw new UpdateFailedException("updates_unavailable"));
                if (!await Updater.PrepareAsync(envelope.CommandId, update, Version, fetch, timeout.Token))
                {
                    return new CommandResult(envelope.CommandId, CommandOutcome.Succeeded, null,
                        System.Text.Json.JsonSerializer.SerializeToElement(new { from = Version, to = Version }), Elapsed(started));
                }
                logger.LogInformation("Command {CommandId}: version {Version} is unpacked and checked; restarting into it", envelope.CommandId, update.Version);
                RestartRequested = true;
                return null;
            }
            catch (UpdateFailedException e)
            {
                logger.LogWarning("Command {CommandId}: update to {Version} refused: {Reason}", envelope.CommandId, update.Version, e.Message);
                return new CommandResult(envelope.CommandId, CommandOutcome.Failed, e.Message, null, Elapsed(started));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new CommandResult(envelope.CommandId, CommandOutcome.Failed, "timeout", null, Elapsed(started));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException)
            {
                logger.LogWarning(e, "Command {CommandId}: update to {Version} failed", envelope.CommandId, update.Version);
                return new CommandResult(envelope.CommandId, CommandOutcome.Failed, e.GetType().Name, null, Elapsed(started));
            }
        }

        private static int Elapsed(long started) => (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        private async Task EnrollIfNeededAsync(CancellationToken cancellationToken)
        {
            if (Store.IsEnrolled)
            {
                return;
            }
            var token = await TokenAsync(cancellationToken)
                ?? throw new AgentRefusedException("not enrolled, and no Agent:EnrollmentToken or Agent:EnrollmentTokenFile: ask an admin for an enrollment token");
            var hostname = Dns.GetHostName();
            var (key, csr) = AgentStore.NewKey(hostname);
            using var client = new PlatformClient(options.Value, null);
            var response = await client.EnrollAsync(new EnrollRequest(token, csr, hostname, RuntimeInformation.OSDescription, Version), cancellationToken);
            Store.SaveEnrollment(key, response);
            Store.RecordToken(token);
            logger.LogInformation("Enrolled as agent {AgentId}", response.AgentId);
        }

        /// <summary>Renews over the current connection when a third of the certificate's life is left. True when it did, so the caller reconnects with the new one.</summary>
        private async Task<bool> RenewIfDueAsync(Connection connection, CancellationToken cancellationToken)
        {
            var current = connection.Certificate;
            var life = current.NotAfter - current.NotBefore;
            if (clock.GetUtcNow() < new DateTimeOffset(current.NotAfter.ToUniversalTime()) - life * RenewAt)
            {
                return false;
            }
            var (key, csr) = AgentStore.NewKey(Dns.GetHostName());
            var response = await connection.Client.RenewAsync(new RenewRequest(csr), cancellationToken);
            Store.SaveRenewal(key, response.CertificatePem);
            logger.LogInformation("Renewed the agent certificate");
            return true;
        }
    }
}
