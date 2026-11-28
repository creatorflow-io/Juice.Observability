using System.Diagnostics;
using System.Security.Claims;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Juice.Observability.Api.Evidence
{
    public sealed record EvidenceSectionsDto(IReadOnlyList<EvidenceSectionDto> Items);

    /// <summary>
    /// <see cref="State"/> is <c>ok</c>, <c>failed</c>, <c>timeout</c> or <c>invalid</c>. A section that isn't <c>ok</c> carries no title, note or items, and
    /// never a message: what went wrong is in the host's log.
    /// </summary>
    public sealed record EvidenceSectionDto(string Key, string Module, string State, string? Title, string? Note, bool Truncated, IReadOnlyList<EvidenceItem> Items);

    /// <summary>
    /// Runs the contributors for one subject the caller has already shown to be in the route tenant, one after the other. Each runs in a scope of its own, on a
    /// connection of its own (its platform context and its module contexts), never the request's: the session is set read-only before a transaction
    /// begins, so a contributor that commits can't write afterwards, and the transaction is never committed. A contributor given up on (its timeout, or the
    /// client leaving) has its statement cancelled from another connection, and its backend ended if it still runs after a short grace; its scope is
    /// disposed in the background once its task has ended, and the request never touches that connection again. The read-only session guards the honest
    /// path: a contributor is trusted in-process code (W-15), and hostile SQL could change the session setting.
    /// </summary>
    internal sealed class EvidenceRunner(IServiceScopeFactory scopes, PlatformDbContext db, IAuthorizationService authorization, EvidenceCatalog catalog,
        IOptions<EvidenceOptions> options, ILogger<EvidenceRunner> logger)
    {
        /// <summary>How long a statement that was asked to stop may take to, before its backend is ended.</summary>
        internal static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(500);

        public async Task<EvidenceSectionsDto> RunAsync(EvidenceContext context, ClaimsPrincipal user, CancellationToken ct)
        {
            // A caller a contributor's policy denies gets no section, and nothing says there was one.
            var allowed = new List<EvidenceContributorInfo>();
            foreach (var info in catalog.For(context.Target))
            {
                if ((await authorization.AuthorizeAsync(user, null, info.Policy ?? CorePolicies.Read)).Succeeded)
                {
                    allowed.Add(info);
                }
            }
            var (timeout, budget) = (options.Value.ContributorTimeout, options.Value.Budget);
            var connectionString = allowed.Count == 0 ? null : db.Database.GetConnectionString();
            var spent = Stopwatch.StartNew();
            var sections = new List<EvidenceSectionDto>();
            foreach (var info in allowed)
            {
                var remaining = budget - spent.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    sections.Add(Failed(info, "timeout"));
                    continue;
                }
                var (section, outcome) = await CallAsync(info, context, connectionString!, TimeSpan.FromTicks(Math.Min(timeout.Ticks, remaining.Ticks)), ct);
                if (outcome != Outcome.Ok)
                {
                    sections.Add(Failed(info, outcome == Outcome.Timeout ? "timeout" : "failed"));
                }
                else if (section is not null)
                {
                    sections.Add(section);
                }
            }
            return new EvidenceSectionsDto(sections);
        }

        private enum Outcome { Ok, Failed, Timeout }

        private async Task<(EvidenceSectionDto? Section, Outcome Outcome)> CallAsync(EvidenceContributorInfo info, EvidenceContext context, string connectionString,
            TimeSpan allowed, CancellationToken ct)
        {
            var scope = scopes.CreateAsyncScope();
            var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            PlatformDbContext? core = null;
            Task<EvidenceSection?>? call = null;
            var pid = 0;
            var givenUp = false;
            try
            {
                core = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await core.Database.OpenConnectionAsync(ct);
                pid = ((NpgsqlConnection)core.Database.GetDbConnection()).ProcessID;
                // The session first, then the transaction: a commit inside the contributor leaves the session read-only for what follows.
                await core.Database.ExecuteSqlRawAsync("SET SESSION CHARACTERISTICS AS TRANSACTION READ ONLY", ct);
                await core.Database.BeginTransactionAsync(ct);
                await core.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
                // Resolved after the transaction begins, so the module contexts join it.
                var contributor = (IEvidenceContributor)scope.ServiceProvider.GetRequiredService(info.Type);
                limit.CancelAfter(allowed);
                var token = limit.Token;
                // On its own thread, so one that blocks without awaiting still meets its timeout.
                call = Task.Run(() => contributor.ContributeAsync(context, token), CancellationToken.None);
                var section = await call.WaitAsync(allowed, ct);
                if (section is null)
                {
                    return (null, Outcome.Ok);
                }
                var checkedSection = EvidenceSanitizer.Check(section, catalog.RouteMatches);
                if (checkedSection.Section is null)
                {
                    logger.LogWarning("Evidence contributor {Key} returned a section the core refused (text or severity out of bounds).", info.Key);
                    return (Failed(info, "invalid"), Outcome.Ok);
                }
                var s = checkedSection.Section;
                return (new EvidenceSectionDto(info.Key, info.Module, "ok", s.Title, s.Note, checkedSection.Truncated, s.Items), Outcome.Ok);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                givenUp = call is { IsCompleted: false };
                if (givenUp)
                {
                    GiveUp(info, call!, scope, core, limit, pid, connectionString);
                }
                throw;
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Evidence contributor {Key} timed out.", info.Key);
                givenUp = true;
                GiveUp(info, call!, scope, core, limit, pid, connectionString);
                return (null, Outcome.Timeout);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Evidence contributor {Key} timed out.", info.Key);
                return (null, Outcome.Timeout);
            }
            catch (Exception e)
            {
                // The exception goes to the log only: its text can hold a query, a path or a secret.
                logger.LogWarning(e, "Evidence contributor {Key} failed.", info.Key);
                return (null, Outcome.Failed);
            }
            finally
            {
                // Nothing runs on the connection any more (the contributor ended, or never started): roll back and dispose here.
                if (!givenUp && call is not { IsCompleted: false })
                {
                    await EndAsync(scope, core, limit);
                }
                else if (!givenUp)
                {
                    GiveUp(info, call!, scope, core, limit, pid, connectionString);
                }
            }
        }

        /// <summary>Never commits, resets the session setting for a pool that doesn't, and disposes; every step is guarded.</summary>
        private async Task EndAsync(AsyncServiceScope scope, PlatformDbContext? core, CancellationTokenSource limit)
        {
            try
            {
                if (core?.Database.CurrentTransaction is { } open)
                {
                    await open.RollbackAsync(CancellationToken.None);
                }
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Evidence transaction rollback failed.");
            }
            try
            {
                if (core is not null && core.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                {
                    await core.Database.ExecuteSqlRawAsync("RESET default_transaction_read_only", CancellationToken.None);
                }
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Evidence session reset failed.");
            }
            try
            {
                await scope.DisposeAsync();
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Evidence scope dispose failed.");
            }
            try
            {
                limit.Dispose();
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Evidence token source dispose failed.");
            }
        }

        /// <summary>
        /// Stops what a contributor still runs and cleans up behind it, away from the request: the token is cancelled, its statement cancelled from another
        /// connection, its backend ended if it still runs after the grace, and the scope disposed when its task ends. The request doesn't touch that connection again.
        /// </summary>
        private void GiveUp(EvidenceContributorInfo info, Task call, AsyncServiceScope scope, PlatformDbContext? core, CancellationTokenSource limit, int pid, string connectionString)
        {
            try
            {
                limit.Cancel();
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Evidence token cancel failed.");
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    if (pid != 0)
                    {
                        await StopBackendAsync(pid, connectionString, terminate: false);
                        await Task.WhenAny(call, Task.Delay(Grace));
                        if (!call.IsCompleted)
                        {
                            logger.LogWarning("Evidence contributor {Key} didn't stop after its statement was cancelled; its backend is ended.", info.Key);
                            await StopBackendAsync(pid, connectionString, terminate: true);
                        }
                    }
                    try
                    {
                        // The connection is ours until we dispose it, so the process id is still the contributor's.
                        await call;
                    }
                    catch (Exception)
                    {
                        // Its failure was reported as the timeout.
                    }
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Evidence contributor {Key}: cleanup after giving up failed.", info.Key);
                }
                finally
                {
                    await EndAsync(scope, core, limit);
                }
            });
        }

        /// <summary>From a connection of its own: <c>pg_cancel_backend</c>, or <c>pg_terminate_backend</c>, of a backend of this database.</summary>
        private async Task StopBackendAsync(int pid, string connectionString, bool terminate)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cts.Token);
                await using var command = new NpgsqlCommand(
                    $"SELECT {(terminate ? "pg_terminate_backend" : "pg_cancel_backend")}(pid) FROM pg_stat_activity WHERE pid = @pid AND datname = current_database()", connection);
                command.Parameters.AddWithValue("pid", pid);
                await command.ExecuteScalarAsync(cts.Token);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Evidence: a contributor's backend couldn't be {Action}.", terminate ? "ended" : "cancelled");
            }
        }

        private static EvidenceSectionDto Failed(EvidenceContributorInfo info, string state) => new(info.Key, info.Module, state, null, null, false, []);
    }
}
