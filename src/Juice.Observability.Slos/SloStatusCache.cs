using System.Collections.Concurrent;
using System.Data.Common;
using Juice.Observability.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Slos
{
    /// <summary>
    /// The SLO list's reads, made once per window end and set of SLOs and shared by every request in that minute on this replica (Phase 9, DR-4).
    /// The statuses are still computed per request, against the versions it read: only the sums are shared.
    /// </summary>
    /// <remarks>
    /// The windows move once a minute, so a read is the same for the whole minute except when a late minute is recounted: a status can be up to a
    /// minute behind that recount. The burn alerts and an SLO's own page don't use it and read fresh.
    /// <para>
    /// The shared read runs in its own scope, detached from the request that started it, within <see cref="ReadTimeout"/>, so a request cancelled
    /// while others wait on it doesn't cancel it for them. A transient failure (not a timeout) is tried once more after a short pause, and every
    /// request waiting on it shares both tries, so a slow or failing database is never met with a read per request. One that fails isn't kept: the requests waiting on it fail, and
    /// the next request starts another. A read is dropped once its minute is over. Disposing the cache (the host's provider, once requests have
    /// drained) stops a read still under way; a request waiting on it then, or coming after, reads for itself. Its token source has no timer, so it isn't disposed.
    /// </para>
    /// </remarks>
    public sealed class SloStatusCache(IServiceScopeFactory scopes, IOptions<SloOptions> options) : IDisposable
    {
        /// <summary>Reads kept for one window end. Past it (many filter or page combinations in one minute), requests read for themselves.</summary>
        internal const int MaxEntries = 64;

        /// <summary>A clock stepped back further than this starts the reads afresh; less, and requests read for themselves until it catches up.</summary>
        private static readonly TimeSpan StepBack = TimeSpan.FromMinutes(5);

        /// <summary>A shared read for these SLOs (sorted, distinct): the key holds only their hash.</summary>
        private sealed record Shared(Guid[] Ids, Lazy<Task<SloReader.Read>> Read);

        private readonly ConcurrentDictionary<(DateTimeOffset End, int Ids), Shared> _reads = new();
        private readonly CancellationTokenSource _disposed = new();
        private readonly Lock _endLock = new();
        private DateTimeOffset _end;
        private int _count;
        private int _misses;

        /// <summary>The pause before a shared read's second try.</summary>
        private static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(250);

        /// <summary>How long a shared read may take, with its second try.</summary>
        internal TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Reads made from the database (each try of a shared read, and each request's own), for tests.</summary>
        internal int Misses => Volatile.Read(ref _misses);

        /// <inheritdoc cref="SloReader.StatusesWithBurnsAsync"/>
        public async Task<Dictionary<Guid, SloStatus>> StatusesWithBurnsAsync(PlatformDbContext db, IReadOnlyList<Guid> ids,
            Func<Guid, IReadOnlyList<SloVersion>> versionsOf, DateTimeOffset now, Func<Guid, IReadOnlySet<int>?> burnVersions, CancellationToken ct)
        {
            if (ids.Count == 0) return [];
            var end = SloReader.End(now);
            var sorted = ids.Distinct().Order().ToArray();
            SloReader.Read read;
            // Disposed (the host stopping): a request that still comes reads for itself.
            if (!_disposed.IsCancellationRequested && Current(end) && Entry((end, Hash(sorted)), sorted, now) is { } shared)
            {
                try
                {
                    read = await shared.Read.Value.WaitAsync(ct);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    // This request was cancelled, whatever became of the read.
                    ct.ThrowIfCancellationRequested();
                    throw;
                }
                catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
                {
                    // Disposed while it waited: it reads for itself, as a request after that does.
                    read = await OwnReadAsync(db, sorted, now, ct);
                }
            }
            else
            {
                read = await OwnReadAsync(db, sorted, now, ct);
            }
            return SloReader.WithBurns(ids, versionsOf, options.Value, read, burnVersions);
        }

        private Task<SloReader.Read> OwnReadAsync(PlatformDbContext db, Guid[] sorted, DateTimeOffset now, CancellationToken ct)
        {
            Interlocked.Increment(ref _misses);
            return SloReader.ReadAsync(db, sorted, now, burnOnly: false, ct);
        }

        private static int Hash(Guid[] sorted)
        {
            var hash = new HashCode();
            foreach (var id in sorted) hash.Add(id);
            return hash.ToHashCode();
        }

        /// <summary>The shared read for the key, started if it isn't yet; none once a minute's entries are all taken, or for another set with the same hash.</summary>
        private Shared? Entry((DateTimeOffset End, int Ids) key, Guid[] sorted, DateTimeOffset now)
        {
            while (true)
            {
                if (_reads.TryGetValue(key, out var existing)) return existing.Ids.AsSpan().SequenceEqual(sorted) ? existing : null;
                if (Interlocked.Increment(ref _count) > MaxEntries)
                {
                    Interlocked.Decrement(ref _count);
                    return null;
                }
                Shared mine = null!;
                mine = new Shared(sorted, new(() => Start(key, mine, now)));
                if (_reads.TryAdd(key, mine))
                {
                    // The windows moved on while it was added: it's still this request's, but no one else will read it.
                    if (!IsCurrent(key.End)) Remove(key, mine);
                    return mine;
                }
                // Another request added one first: share it.
                Interlocked.Decrement(ref _count);
            }
        }

        private Task<SloReader.Read> Start((DateTimeOffset, int) key, Shared entry, DateTimeOffset now)
        {
            // Not under the request's context, nor what's left after it: its trace, logging scope and HttpContext don't belong to a read other
            // requests share, or to the timer that drops it.
            if (ExecutionContext.IsFlowSuppressed()) return Detached(key, entry, now);
            using (ExecutionContext.SuppressFlow()) return Detached(key, entry, now);
        }

        private Task<SloReader.Read> Detached((DateTimeOffset, int) key, Shared entry, DateTimeOffset now)
        {
            var task = Task.Run(() => LoadAsync(key, entry, now));
            // Observed even when every request waiting on it has gone.
            _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            // Dropped once its minute is over, even when no request comes to move the windows on.
            var left = SloCounter.Floor(now) + TimeSpan.FromMinutes(1) - now + TimeSpan.FromSeconds(1);
            _ = Task.Delay(left, _disposed.Token).ContinueWith(_ => Remove(key, entry), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
            return task;
        }

        private async Task<SloReader.Read> LoadAsync((DateTimeOffset, int) key, Shared entry, DateTimeOffset now)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token);
                timeout.CancelAfter(ReadTimeout);
                for (var attempt = 0; ; attempt++)
                {
                    Interlocked.Increment(ref _misses);
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                        return await SloReader.ReadAsync(db, entry.Ids, now, burnOnly: false, timeout.Token);
                    }
                    catch (Exception e) when (attempt == 0 && !timeout.IsCancellationRequested
                        && e is DbException { IsTransient: true, InnerException: not TimeoutException })
                    {
                        // A transient failure, once more after a pause, for every request waiting on it. One that would fail the same way again,
                        // or one that timed out (a saturated database or pool, which a second try would only load more), isn't tried again.
                        await Task.Delay(RetryPause, timeout.Token);
                    }
                }
            }
            catch
            {
                // Not kept: dropped before its requests see it fail, so the next request starts another.
                Remove(key, entry);
                throw;
            }
        }

        private void Remove((DateTimeOffset, int) key, Shared entry)
        {
            if (_reads.TryRemove(KeyValuePair.Create(key, entry))) Interlocked.Decrement(ref _count);
        }

        private bool IsCurrent(DateTimeOffset end)
        {
            lock (_endLock) return end == _end;
        }

        /// <summary>
        /// Whether reads for windows ending at <paramref name="end"/> are shared: those of the newest end seen. Moving on drops the older ones; a
        /// request still on an older minute reads for itself, so nothing is kept that no one will read again.
        /// </summary>
        private bool Current(DateTimeOffset end)
        {
            bool back;
            lock (_endLock)
            {
                if (end == _end) return true;
                if (end < _end && end > _end - StepBack) return false;
                back = end < _end;
                _end = end;
            }
            // Only older ends when moving on, so a minute that has moved on again meanwhile keeps its reads; every other end after a step back.
            foreach (var (key, entry) in _reads)
            {
                if (back ? key.End != end : key.End < end) Remove(key, entry);
            }
            return true;
        }

        public void Dispose() => _disposed.Cancel();
    }
}
