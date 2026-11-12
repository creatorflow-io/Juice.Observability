using Juice.Observability.Data;
using Juice.Observability.Slos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>Phase 9 DR-4: the SLO list's sums are read once per window end and set of SLOs, and shared.</summary>
    public class SloStatusCacheTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 44, 10, TimeSpan.Zero);

        private static async Task<(Slo Slo, List<SloVersion> Versions)> SeedAsync(PlatformDbContext db)
        {
            var tenant = (await db.Tenants.SingleAsync(Ct)).Id;
            var created = Now - TimeSpan.FromDays(10);
            var slo = new Slo { TenantId = tenant, Key = "cached", Name = "cached", Sli = "Availability", Service = "shop/orders", Environment = "production", CurrentVersion = 1, CreatedAt = created };
            var version = new SloVersion { TenantId = tenant, SloId = slo.Id, Version = 1, Definition = "{}", Hash = "h1", Target = 99.5, EffectiveFrom = created, CreatedAt = created, CreatedBy = "test" };
            db.AddRange(slo, version);
            await db.SaveChangesAsync(Ct);
            return (slo, [version]);
        }

        /// <summary>An hour of all-good minutes, with their totals as the counter keeps them.</summary>
        private static async Task CountHourAsync(PlatformDbContext db, Slo slo, DateTimeOffset from)
        {
            for (var minute = from; minute < from + TimeSpan.FromHours(1); minute += TimeSpan.FromMinutes(1))
            {
                db.SloBuckets.Add(new SloBucket { TenantId = slo.TenantId, SloId = slo.Id, Minute = minute, Version = 1, Total = 100, Good = 100, Eligible = 100, CountedAt = minute });
            }
            await db.SaveChangesAsync(Ct);
            await SloDays.RecomputeAsync(db, slo.Id, from, from + TimeSpan.FromHours(1), Ct);
        }

        /// <summary>The cache as the API has it, a singleton the provider disposes.</summary>
        private static ServiceProvider Services(ScratchDatabase database, Func<PlatformDbContext>? context = null)
            => new ServiceCollection().AddScoped(_ => (context ?? database.Context)()).AddSingleton(Options.Create(new SloOptions()))
                .AddSingleton<SloStatusCache>().BuildServiceProvider();

        [Fact]
        public async Task The_list_reads_once_per_window_minute_and_shares_it_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var db = database.Context();
            var (slo, versions) = await SeedAsync(db);
            var end = SloReader.End(Now);
            await CountHourAsync(db, slo, end - TimeSpan.FromHours(1));
            await using var services = Services(database);
            var cache = services.GetRequiredService<SloStatusCache>();
            async Task<long> TotalAsync(DateTimeOffset now, IReadOnlyList<Guid>? ids = null, CancellationToken? ct = null)
                => (await cache.StatusesWithBurnsAsync(db, ids ?? [slo.Id], _ => versions, now, _ => null, ct ?? Ct))[slo.Id].Budget.Total;

            Assert.Equal(6000, await TotalAsync(Now));
            Assert.Equal(1, cache.Misses);

            // An hour recounted late, in the same minute: not seen until the windows move.
            await CountHourAsync(db, slo, end - TimeSpan.FromHours(2));
            Assert.Equal(6000, await TotalAsync(Now + TimeSpan.FromSeconds(20)));
            Assert.Equal(1, cache.Misses);
            Assert.Equal(12000, await TotalAsync(Now + TimeSpan.FromMinutes(1)));
            Assert.Equal(2, cache.Misses);
            // A request still on the minute before reads for itself, each time, keeping nothing.
            Assert.Equal(12000, await TotalAsync(Now));
            Assert.Equal(12000, await TotalAsync(Now));
            Assert.Equal(4, cache.Misses);
            Assert.Equal(12000, await TotalAsync(Now + TimeSpan.FromMinutes(1)));
            Assert.Equal(4, cache.Misses);
            // A page with no SLOs reads nothing.
            Assert.Empty(await cache.StatusesWithBurnsAsync(db, [], _ => versions, Now + TimeSpan.FromMinutes(1), _ => null, Ct));
            Assert.Equal(4, cache.Misses);

            // Requests arriving together at a new minute share one read.
            var together = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => TotalAsync(Now + TimeSpan.FromMinutes(2))));
            Assert.All(together, total => Assert.Equal(12000, total));
            Assert.Equal(5, cache.Misses);

            // A request cancelled while waiting doesn't cancel the read for the others.
            using (var cancelled = new CancellationTokenSource())
            {
                await cancelled.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TotalAsync(Now + TimeSpan.FromMinutes(3), ct: cancelled.Token));
            }
            Assert.Equal(12000, await TotalAsync(Now + TimeSpan.FromMinutes(3)));
            Assert.Equal(6, cache.Misses);

            // The same SLOs in another order are the same read; another set is its own.
            var other = Guid.NewGuid();
            Assert.Equal(12000, await TotalAsync(Now + TimeSpan.FromMinutes(3), [other, slo.Id]));
            Assert.Equal(12000, await TotalAsync(Now + TimeSpan.FromMinutes(3), [slo.Id, other, slo.Id]));
            Assert.Equal(7, cache.Misses);

            // Past the entries kept for a minute, a request reads for itself every time, and still right.
            for (var i = 0; i < SloStatusCache.MaxEntries; i++) await TotalAsync(Now + TimeSpan.FromMinutes(4), [slo.Id, Guid.NewGuid()]);
            var misses = cache.Misses;
            Assert.Equal(12000, await TotalAsync(Now + TimeSpan.FromMinutes(4)));
            Assert.Equal(12000, await TotalAsync(Now + TimeSpan.FromMinutes(4)));
            Assert.Equal(misses + 2, cache.Misses);
            // The next minute starts empty.
            Assert.Equal(12000, await TotalAsync(Now + TimeSpan.FromMinutes(5)));
            Assert.Equal(12000, await TotalAsync(Now + TimeSpan.FromMinutes(5)));
            Assert.Equal(misses + 3, cache.Misses);
        }

        [Fact]
        public async Task A_failed_read_is_not_kept_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var db = database.Context();
            var (slo, versions) = await SeedAsync(db);
            await CountHourAsync(db, slo, SloReader.End(Now) - TimeSpan.FromHours(1));
            async Task<(SloStatusCache Cache, List<Task<long>> Waiting)> BurstAsync(ServiceProvider services, TaskCompletionSource gate, DateTimeOffset now)
            {
                var cache = services.GetRequiredService<SloStatusCache>();
                // Five requests waiting together, then the read they share fails.
                var waiting = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
                    (await cache.StatusesWithBurnsAsync(db, [slo.Id], _ => versions, now, _ => null, Ct))[slo.Id].Budget.Total, Ct)).ToList();
                await Task.Delay(200, Ct);
                gate.SetResult();
                return (cache, waiting);
            }
            Func<PlatformDbContext> Failing(int times, TaskCompletionSource gate, bool transient = true) => () =>
            {
                if (Interlocked.Decrement(ref times) < 0) return database.Context();
                gate.Task.Wait(Ct);
                throw transient ? new Down() : new InvalidOperationException("broken");
            };

            // A failure that would happen again: not tried again, and the requests fail.
            var broken = new TaskCompletionSource();
            await using (var services = Services(database, Failing(1, broken, transient: false)))
            {
                var (cache, waiting) = await BurstAsync(services, broken, Now);
                foreach (var request in waiting) Assert.Equal("broken", (await Assert.ThrowsAsync<InvalidOperationException>(() => request)).Message);
                Assert.Equal(1, cache.Misses);
            }

            // A transient one, once: the second try, inside the same shared read, serves them all; never a read each.
            var once = new TaskCompletionSource();
            await using (var services = Services(database, Failing(1, once)))
            {
                var (cache, waiting) = await BurstAsync(services, once, Now);
                Assert.All(await Task.WhenAll(waiting), total => Assert.Equal(6000, total));
                Assert.Equal(2, cache.Misses);
                Assert.Equal(6000, (await cache.StatusesWithBurnsAsync(db, [slo.Id], _ => versions, Now, _ => null, Ct))[slo.Id].Budget.Total);
                Assert.Equal(2, cache.Misses);
            }

            // Down: both tries fail and so do the requests, none reading for itself (which would succeed: only the shared reads' scope is down).
            var down = new TaskCompletionSource();
            await using (var services = Services(database, Failing(int.MaxValue, down)))
            {
                var (cache, waiting) = await BurstAsync(services, down, Now);
                foreach (var request in waiting) await Assert.ThrowsAsync<Down>(() => request);
                Assert.Equal(2, cache.Misses);
                // And the failed read isn't kept: the next request starts another.
                await Assert.ThrowsAsync<Down>(() => cache.StatusesWithBurnsAsync(db, [slo.Id], _ => versions, Now, _ => null, Ct));
                Assert.Equal(4, cache.Misses);
            }
        }

        /// <summary>A failure the database says may pass if tried again.</summary>
        private sealed class Down() : System.Data.Common.DbException("down")
        {
            public override bool IsTransient => true;
        }

        [Fact]
        public async Task A_shared_read_stops_when_the_cache_is_disposed_and_on_its_timeout_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var db = database.Context();
            var (slo, versions) = await SeedAsync(db);
            await CountHourAsync(db, slo, SloReader.End(Now) - TimeSpan.FromHours(1));
            Task<Dictionary<Guid, SloStatus>> ReadAsync(SloStatusCache cache) => cache.StatusesWithBurnsAsync(db, [slo.Id], _ => versions, Now, _ => null, Ct);

            // Disposed while the read is under way: it's cancelled, not tried again, and the request waiting on it reads for itself.
            var (opened, released) = (new TaskCompletionSource(), new TaskCompletionSource());
            await using (var services = Services(database, () =>
            {
                opened.TrySetResult();
                released.Task.Wait(TimeSpan.FromSeconds(10));
                return database.Context();
            }))
            {
                var cache = services.GetRequiredService<SloStatusCache>();
                var request = ReadAsync(cache);
                await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
                cache.Dispose();
                released.SetResult();
                Assert.Equal(6000, (await request)[slo.Id].Budget.Total);
                Assert.Equal(2, cache.Misses);   // the shared read, stopped, and the request's own
                // A request after that reads for itself.
                Assert.Equal(6000, (await ReadAsync(cache))[slo.Id].Budget.Total);
                Assert.Equal(3, cache.Misses);
            }

            // A read past its timeout fails the same way.
            await using (var services = Services(database, () =>
            {
                Thread.Sleep(200);
                return database.Context();
            }))
            {
                var cache = services.GetRequiredService<SloStatusCache>();
                cache.ReadTimeout = TimeSpan.FromMilliseconds(50);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(cache));
                Assert.Equal(1, cache.Misses);
                // And isn't kept.
                cache.ReadTimeout = TimeSpan.FromSeconds(30);
                Assert.Equal(6000, (await ReadAsync(cache))[slo.Id].Budget.Total);
            }
        }
    }
}
