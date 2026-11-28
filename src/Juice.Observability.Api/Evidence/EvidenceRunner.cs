using System.Diagnostics;
using System.Security.Claims;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api.Evidence
{
    public sealed record EvidenceSectionsDto(IReadOnlyList<EvidenceSectionDto> Items);

    /// <summary>
    /// <see cref="State"/> is <c>ok</c>, <c>failed</c>, <c>timeout</c> or <c>invalid</c>. A section that isn't <c>ok</c> carries no title, note or items, and
    /// never a message: what went wrong is in the host's log.
    /// </summary>
    public sealed record EvidenceSectionDto(string Key, string Module, string State, string? Title, string? Note, bool Truncated, IReadOnlyList<EvidenceItem> Items);

    /// <summary>
    /// Runs the contributors for one subject the caller has already shown to be in the route tenant. All of them run in one read-only transaction on the
    /// request's connection (the module contexts of <c>AddModuleData</c> join it), each in a savepoint of its own that is always rolled back, one after the
    /// other, each under its timeout and within the total budget. The transaction is never committed.
    /// </summary>
    internal sealed class EvidenceRunner(IServiceProvider services, PlatformDbContext db, IAuthorizationService authorization, EvidenceCatalog catalog,
        IOptions<EvidenceOptions> options, ILogger<EvidenceRunner> logger)
    {
        /// <summary>How long a contributor that ignores its token is waited for after its timeout, before the rest are given up.</summary>
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
            if (allowed.Count == 0)
            {
                return new EvidenceSectionsDto([]);
            }

            var (timeout, budget) = (options.Value.ContributorTimeout, options.Value.Budget);
            var spent = Stopwatch.StartNew();
            var sections = new List<EvidenceSectionDto>();
            var transaction = await db.Database.BeginTransactionAsync(ct);
            var abandoned = false;
            try
            {
                // First statement: nothing a contributor runs, in any context on this connection, can write.
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
                var broken = false;
                for (var i = 0; i < allowed.Count; i++)
                {
                    var info = allowed[i];
                    var remaining = budget - spent.Elapsed;
                    if (broken || abandoned || remaining <= TimeSpan.Zero)
                    {
                        sections.Add(Failed(info, broken ? "failed" : "timeout"));
                        continue;
                    }
                    var savepoint = $"evidence_{i}";
                    await transaction.CreateSavepointAsync(savepoint, ct);
                    var (section, outcome) = await CallAsync(info, context, TimeSpan.FromTicks(Math.Min(timeout.Ticks, remaining.Ticks)), ct);
                    if (outcome == Outcome.Abandoned)
                    {
                        // Still running on the shared connection: nothing more may touch it.
                        abandoned = true;
                        sections.Add(Failed(info, "timeout"));
                        continue;
                    }
                    try
                    {
                        // Rolled back whatever happened: a read has nothing to keep, a failed statement leaves the transaction aborted until this, and a write
                        // that somehow got through is undone before the next contributor runs.
                        await transaction.RollbackToSavepointAsync(savepoint, ct);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        logger.LogWarning(e, "Evidence contributor {Key}: the savepoint couldn't be rolled back; the rest of the sections are skipped.", info.Key);
                        broken = true;
                        sections.Add(Failed(info, "failed"));
                        continue;
                    }
                    if (outcome == Outcome.Ok && section is not null)
                    {
                        sections.Add(section);
                    }
                    else if (outcome != Outcome.Ok)
                    {
                        sections.Add(Failed(info, outcome == Outcome.Timeout ? "timeout" : "failed"));
                    }
                }
            }
            finally
            {
                if (!abandoned)
                {
                    try
                    {
                        // Never committed.
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                    catch (Exception e)
                    {
                        logger.LogDebug(e, "Evidence transaction rollback failed (the connection had already dropped it).");
                    }
                    await transaction.DisposeAsync();
                }
            }
            return new EvidenceSectionsDto(sections);
        }

        private enum Outcome { Ok, Failed, Timeout, Abandoned }

        private async Task<(EvidenceSectionDto? Section, Outcome Outcome)> CallAsync(EvidenceContributorInfo info, EvidenceContext context, TimeSpan allowed, CancellationToken ct)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(allowed);
            Task<EvidenceSection?>? call = null;
            try
            {
                var contributor = (IEvidenceContributor)services.GetRequiredService(info.Type);
                // On its own thread, so one that blocks without awaiting still meets its timeout.
                call = Task.Run(() => contributor.ContributeAsync(context, limit.Token), CancellationToken.None);
                var section = await call.WaitAsync(allowed + Grace, ct);
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
                throw;
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Evidence contributor {Key} didn't stop after its timeout; the rest of the sections are skipped.", info.Key);
                return (null, Outcome.Abandoned);
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
        }

        private static EvidenceSectionDto Failed(EvidenceContributorInfo info, string state) => new(info.Key, info.Module, state, null, null, false, []);
    }
}
