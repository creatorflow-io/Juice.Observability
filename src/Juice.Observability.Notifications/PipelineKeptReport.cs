using Juice.Observability.Data;
using Juice.Observability.Health.DeadMan;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// The dead-man's view of what the platform kept for the pipeline (<see cref="IPipelineKeptReport"/>): <see cref="PipelineStart.Failure"/>,
    /// and the oldest call parked (not replayed) or notification held because the pipeline was down (not decided). A hold under a silence isn't
    /// the pipeline's backlog: the silence keeps it.
    /// </summary>
    internal sealed class PipelineKeptReport(PlatformDbContext db, PipelineStart? start = null) : IPipelineKeptReport
    {
        public string? StartFailure => start?.Failure;

        public async ValueTask<DateTimeOffset?> OldestKeptAsync(CancellationToken cancellationToken)
        {
            var parked = await db.NotificationParkedCalls.AsNoTracking().IgnoreQueryFilters().Where(c => c.ReplayedAt == null)
                .OrderBy(c => c.CreatedAt).Select(c => (DateTimeOffset?)c.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            var held = await db.NotificationHolds.AsNoTracking().IgnoreQueryFilters().Where(h => h.DecidedAt == null && h.SilenceId == ProducerAdapter.PipelineUnavailable)
                .OrderBy(h => h.CreatedAt).Select(h => (DateTimeOffset?)h.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            return parked is null ? held : held is null ? parked : parked < held ? parked : held;
        }
    }
}
