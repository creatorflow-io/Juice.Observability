using Juice.Observability.Data;
using Juice.Observability.Slos;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Tests
{
    /// <summary>Phase 9 milestone 4: reading the windows from daily totals plus the partial days' minutes gives what summing every minute gives.</summary>
    public class SloDayTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task Windows_from_daily_totals_equal_the_sum_of_their_minutes_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var db = database.Context();
            var tenant = (await db.Tenants.SingleAsync(Ct)).Id;
            var created = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            var slo = new Slo { TenantId = tenant, Key = "days", Name = "days", Sli = "Availability", Service = "shop/orders", Environment = "production", CurrentVersion = 2, CreatedAt = created };
            // Version 2 from a time that isn't a midnight, so windows split between versions inside a day.
            var change = new DateTimeOffset(2026, 10, 20, 13, 37, 0, TimeSpan.Zero);
            SloVersion Version(int n, double target, DateTimeOffset from) => new()
            {
                TenantId = tenant, SloId = slo.Id, Version = n, Definition = "{}", Hash = $"h{n}", Target = target, EffectiveFrom = from, CreatedAt = from, CreatedBy = "test",
            };
            var versions = new List<SloVersion> { Version(1, 99.5, created), Version(2, 99.9, change) };
            db.AddRange(slo, versions[0], versions[1]);

            // 31 days of minutes, a few missing, counts varying by minute.
            var random = new Random(7);
            var from = new DateTimeOffset(2026, 10, 2, 5, 11, 0, TimeSpan.Zero);
            var until = new DateTimeOffset(2026, 11, 2, 0, 30, 0, TimeSpan.Zero);
            var buckets = new List<SloBucket>();
            for (var minute = from; minute < until; minute += TimeSpan.FromMinutes(1))
            {
                if (random.Next(50) == 0) continue;   // a gap
                var total = random.Next(0, 300);
                var good = total - random.Next(0, Math.Max(1, total / 20));
                buckets.Add(new SloBucket
                {
                    TenantId = tenant, SloId = slo.Id, Minute = minute, Version = minute < change ? 1 : 2, Total = total, Good = good,
                    Eligible = total + random.Next(0, 3), Bad = total - good, ExcludedHealth = random.Next(0, 5), CountedAt = minute,
                });
            }
            db.SloBuckets.AddRange(buckets);
            await db.SaveChangesAsync(Ct);
            await SloDays.RecomputeAsync(db, slo.Id, from, until, Ct);

            // Just after midnight, mid-morning, and late: a window's first and last days are partial in different ways.
            foreach (var now in new[] { new DateTimeOffset(2026, 11, 1, 0, 1, 30, TimeSpan.Zero), new DateTimeOffset(2026, 11, 1, 9, 44, 10, TimeSpan.Zero), until })
            {
                var status = (await SloReader.StatusesAsync(db, [slo.Id], _ => versions, new SloOptions(), now, Ct))[slo.Id];
                var end = SloCounter.Floor(now) - SloReader.Settle;
                foreach (var window in status.Windows.Append(status.Budget))
                {
                    var inside = buckets.Where(b => b.Minute >= end - window.Length && b.Minute < end).ToList();
                    Assert.Equal((inside.Sum(b => b.Total), inside.Sum(b => b.Good)), (window.Total, window.Good));
                    Assert.Equal(inside.Sum(b => b.Total * (1 - (b.Version == 1 ? 0.995 : 0.999))), window.Allowed, 6);   // each version at its own target
                    var expected = (end - (from > end - window.Length ? from : end - window.Length)).TotalMinutes;
                    Assert.Equal(Math.Min(1, inside.Count / expected), window.Coverage, 9);
                }
            }

            // The SLO page's days: whole days from their totals, the first partial day from its minutes, every count as the minutes have it.
            var pageNow = new DateTimeOffset(2026, 11, 1, 9, 44, 10, TimeSpan.Zero);
            var pageFrom = SloCounter.Floor(pageNow) - SloOptions.Window;
            var daysShown = await SloReader.DaysAsync(db, slo.Id, pageNow, Ct);
            var expectedDays = buckets.Where(b => b.Minute >= pageFrom).GroupBy(b => b.Minute.UtcDateTime.Date).OrderBy(g => g.Key)
                .Select(g => (g.Key, g.Sum(b => b.Total), g.Sum(b => b.Good), g.Sum(b => b.Eligible), g.Sum(b => b.Bad), (long)g.Count())).ToList();
            Assert.Equal(expectedDays, daysShown.Select(d => (d.Day, d.Total, d.Good, d.Eligible, d.Bad, d.Minutes)));

            // Minutes counted without totals (a build from before them, still holding the lease while this one starts): caught up from the last
            // summed hour on.
            var lastSummed = await db.SloHours.MaxAsync(h => h.Hour, Ct);
            await db.SloHours.Where(h => h.Hour >= lastSummed - TimeSpan.FromHours(5)).ExecuteDeleteAsync(Ct);
            await db.SloDays.Where(d => d.Day >= SloDays.Day(lastSummed - TimeSpan.FromHours(5))).ExecuteDeleteAsync(Ct);
            await SloDays.CatchUpAsync(db, slo.Id, until, TimeSpan.FromMinutes(20), Ct);
            var caughtUp = (await SloReader.StatusesAsync(db, [slo.Id], _ => versions, new SloOptions(), until, Ct))[slo.Id];
            var budgetEnd = SloCounter.Floor(until) - SloReader.Settle;
            var inBudget = buckets.Where(b => b.Minute >= budgetEnd - SloOptions.Window && b.Minute < budgetEnd).ToList();
            Assert.Equal((inBudget.Sum(b => b.Total), inBudget.Sum(b => b.Good)), (caughtUp.Budget.Total, caughtUp.Budget.Good));

            // The older build also counts its newest minutes again, reaching into the hour before the last one summed: caught up as far back as the
            // lookback given.
            var latest = await db.SloHours.MaxAsync(h => h.Hour, Ct);
            var recounted = latest - TimeSpan.FromMinutes(10);
            await db.SloBuckets.Where(b => b.Minute >= recounted && b.Minute < latest).ExecuteUpdateAsync(u => u.SetProperty(b => b.Total, b => b.Total + 1000), Ct);
            await SloDays.CatchUpAsync(db, slo.Id, until, TimeSpan.FromMinutes(20), Ct);
            var hourBefore = latest - TimeSpan.FromHours(1);
            var bucketsThen = await db.SloBuckets.AsNoTracking().Where(b => b.Minute >= hourBefore && b.Minute < latest).SumAsync(b => b.Total, Ct);
            Assert.Equal(bucketsThen, await db.SloHours.Where(h => h.Hour == hourBefore).SumAsync(h => h.Total, Ct));

            // The counter's days follow its buckets: counting a stretch again sums its days again.
            var day = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
            await db.SloBuckets.Where(b => b.Minute >= day + TimeSpan.FromHours(3) && b.Minute < day + TimeSpan.FromHours(4)).ExecuteDeleteAsync(Ct);
            await SloDays.RecomputeAsync(db, slo.Id, day + TimeSpan.FromHours(3), day + TimeSpan.FromHours(4), Ct);
            var stored = await db.SloDays.AsNoTracking().SingleAsync(d => d.Day == day, Ct);
            var left = buckets.Where(b => b.Minute >= day && b.Minute < day + TimeSpan.FromDays(1) && (b.Minute < day + TimeSpan.FromHours(3) || b.Minute >= day + TimeSpan.FromHours(4))).ToList();
            Assert.Equal((left.Sum(b => b.Total), left.Sum(b => b.Good), (long)left.Count), (stored.Total, stored.Good, stored.Minutes));
        }
    }
}
