using System.Collections.Concurrent;

namespace Juice.Observability.Agents
{
    /// <summary>
    /// Enrollment needs no login, so it is the one agent call anyone who can reach the listener can make. This counts its failures per address:
    /// after <see cref="MaxFailures"/> in <see cref="Window"/>, the address is refused before any database work until the window ends. Successful
    /// enrollments don't count, so many hosts enrolling from behind one address aren't held up. Per process: each replica keeps its own count.
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

        public bool IsBlocked(string address)
        {
            var now = clock.GetUtcNow();
            if (_failures.TryGetValue(address, out var entry))
            {
                return now - entry.Start < Window && entry.Failures >= MaxFailures;
            }
            return IsFull(now);
        }

        public void Fail(string address)
        {
            var now = clock.GetUtcNow();
            if (!_failures.ContainsKey(address) && IsFull(now))
            {
                return;   // refused anyway while full; not remembered
            }
            _failures.AddOrUpdate(address, _ => (now, 1), (_, entry) => now - entry.Start >= Window ? (now, 1) : (entry.Start, entry.Failures + 1));
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
