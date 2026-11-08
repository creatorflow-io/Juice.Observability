using System.Collections.Concurrent;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Agents
{
    /// <summary>
    /// Enrollment needs no login, so it is the one agent call anyone who can reach the listener can make. This counts its failures per address:
    /// after <see cref="MaxFailures"/> in <see cref="Window"/>, the address is refused before any database work until the window ends. Successful
    /// enrollments don't count, so many hosts enrolling from behind one address aren't held up. The count is shared by every replica, in
    /// <c>agent_enrollment_failures</c> (<see cref="IsBlockedAsync"/>, <see cref="FailAsync"/>); each replica also keeps its own, checked first, so an
    /// address it already refuses costs no database work.
    /// It remembers at most <see cref="MaxAddresses"/> addresses: when that many have failed within one window, an address it doesn't know is refused
    /// too until some expire, so many addresses at once can't grow it without end. Enrolling may then wait a minute during such a flood.
    /// </summary>
    public sealed class EnrollmentGuard(TimeProvider clock)
    {
        public const int MaxFailures = 10;
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
        internal const int MaxAddresses = 10_000;

        private readonly ConcurrentDictionary<string, (DateTimeOffset Start, int Failures)> _failures = new();
        private readonly object _prune = new();

        private int _sinceCleanup;

        /// <summary>Refused here already, or by the count all replicas share.</summary>
        public async Task<bool> IsBlockedAsync(PlatformDbContext db, string address, CancellationToken cancellationToken)
        {
            if (IsBlocked(address))
            {
                return true;
            }
            var key = Key(address);
            var shared = await db.AgentEnrollmentFailures.AsNoTracking().SingleOrDefaultAsync(f => f.Address == key, cancellationToken);
            return shared is not null && clock.GetUtcNow() - shared.WindowStart < Window && shared.Failures >= MaxFailures;
        }

        /// <summary>
        /// Counts a failure here and in the shared count, in one statement, so two replicas counting at once both count. Stale rows are pruned every
        /// so often. While this replica's own count is full (a flood of addresses), nothing is written: those are refused anyway.
        /// </summary>
        public async Task FailAsync(PlatformDbContext db, string address, CancellationToken cancellationToken)
        {
            if (!Fail(address))
            {
                return;
            }
            var key = Key(address);
            var now = clock.GetUtcNow();
            var expired = now - Window;
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO agent_enrollment_failures (address, window_start, failures) VALUES ({key}, {now}, 1)
                ON CONFLICT (address) DO UPDATE SET
                  failures = CASE WHEN agent_enrollment_failures.window_start <= {expired} THEN 1 ELSE agent_enrollment_failures.failures + 1 END,
                  window_start = CASE WHEN agent_enrollment_failures.window_start <= {expired} THEN {now} ELSE agent_enrollment_failures.window_start END
                """, cancellationToken);
            if (Interlocked.Increment(ref _sinceCleanup) % 100 == 0)
            {
                await db.AgentEnrollmentFailures.Where(f => f.WindowStart <= expired).ExecuteDeleteAsync(cancellationToken);
            }
        }

        private static string Key(string address) => address.Length <= 64 ? address : address[..64];

        public bool IsBlocked(string address)
        {
            var now = clock.GetUtcNow();
            if (_failures.TryGetValue(address, out var entry))
            {
                return now - entry.Start < Window && entry.Failures >= MaxFailures;
            }
            return IsFull(now);
        }

        /// <summary>Counts a failure in this replica's own count. False when it wasn't remembered: full, and refused anyway.</summary>
        public bool Fail(string address)
        {
            var now = clock.GetUtcNow();
            if (!_failures.ContainsKey(address) && IsFull(now))
            {
                return false;   // refused anyway while full; not remembered
            }
            _failures.AddOrUpdate(address, _ => (now, 1), (_, entry) => now - entry.Start >= Window ? (now, 1) : (entry.Start, entry.Failures + 1));
            return true;
        }

        /// <summary>When the earliest remembered failure expires, while full: before then a scan can't free anything.</summary>
        private DateTimeOffset _fullUntil;

        /// <summary>
        /// At the limit even after dropping what has expired. It scans only at the limit, one caller at a time, and not again before the earliest
        /// entry expires, so a flood that keeps it full costs a lookup per request, not a scan.
        /// </summary>
        private bool IsFull(DateTimeOffset now)
        {
            if (_failures.Count < MaxAddresses)
            {
                return false;
            }
            lock (_prune)
            {
                if (now < _fullUntil)
                {
                    return true;
                }
                foreach (var stale in _failures.Where(f => now - f.Value.Start >= Window).Select(f => f.Key).ToList())
                {
                    _failures.TryRemove(stale, out _);
                }
                if (_failures.Count < MaxAddresses)
                {
                    return false;
                }
                _fullUntil = _failures.Values.Min(f => f.Start) + Window;
                return true;
            }
        }
    }
}
