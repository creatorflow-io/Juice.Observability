using System.Diagnostics;
using System.Diagnostics.Metrics;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Discovery
{
    public sealed record DiscoveryRunResult(int Windows, DateTimeOffset Watermark, ReconcileResult Totals);

    /// <summary>
    /// One discovery pass: reads telemetry from the checkpoint up to now minus the lag, in windows of at most
    /// <see cref="DiscoveryOptions.MaxWindow"/>, and reconciles each window and moves the checkpoint in one
    /// transaction, so a window is applied exactly once.
    /// </summary>
    public sealed class DiscoveryRunner(IServiceScopeFactory scopes, ITelemetrySource source, IOptions<DiscoveryOptions> options,
        IOptions<PlatformOptions> platform, TimeProvider clock, ILogger<DiscoveryRunner> logger)
    {
        public const string CheckpointSource = "telemetry";

        internal static readonly Meter Meter = new("Juice.Observability");
        private static readonly Counter<long> Runs = Meter.CreateCounter<long>("juice.observability.discovery.runs", "{run}",
            "Discovery passes, by outcome");
        private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("juice.observability.discovery.duration", "s",
            "Duration of a discovery pass");
        private static readonly Counter<long> Created = Meter.CreateCounter<long>("juice.observability.discovery.created", "{record}",
            "Records created by discovery, by kind");
        private static readonly Counter<long> Truncated = Meter.CreateCounter<long>("juice.observability.discovery.truncated", "{value}",
            "Telemetry values or windows cut to their bounds, by kind");

        public async Task<DiscoveryRunResult> RunOnceAsync(CancellationToken cancellationToken)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var result = await RunAsync(cancellationToken);
                Runs.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
                return result;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                Runs.Add(1, new KeyValuePair<string, object?>("outcome", "failure"));
                throw;
            }
            finally
            {
                Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            }
        }

        private async Task<DiscoveryRunResult> RunAsync(CancellationToken cancellationToken)
        {
            var settings = options.Value;
            var totals = new ReconcileResult(0, 0, 0, 0, 0, 0);
            var windows = 0;
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Key == platform.Value.TenantKey, cancellationToken);
            var end = clock.GetUtcNow() - settings.Lag;
            var watermark = await db.Checkpoints.AsNoTracking()
                .Where(c => c.TenantId == tenant.Id && c.Source == CheckpointSource)
                .Select(c => (DateTimeOffset?)c.Watermark).SingleOrDefaultAsync(cancellationToken)
                ?? end - settings.InitialLookback;

            while (watermark < end && windows < settings.MaxWindowsPerRun)
            {
                var to = Min(watermark + settings.MaxWindow, end);
                var (resources, links, uses) = await ReadAsync(tenant.Key, watermark, to, cancellationToken);

                var normalizer = new Normalizer(settings.Aliases);
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var result = await new Reconciler(db, normalizer, settings, clock).ApplyAsync(tenant.Id, resources, links, uses, cancellationToken);
                var checkpoint = await db.Checkpoints.SingleOrDefaultAsync(c => c.TenantId == tenant.Id && c.Source == CheckpointSource, cancellationToken);
                if (checkpoint is null)
                {
                    db.Checkpoints.Add(new ReconciliationCheckpoint { TenantId = tenant.Id, Source = CheckpointSource, Watermark = to, UpdatedAt = clock.GetUtcNow() });
                }
                else
                {
                    checkpoint.Watermark = to;
                    checkpoint.UpdatedAt = clock.GetUtcNow();
                }
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                db.ChangeTracker.Clear();

                Record(result, normalizer.Truncated);
                totals = Add(totals, result);
                watermark = to;
                windows++;
                logger.LogDebug("Discovery window up to {Watermark}: {Resources} resource sets, {Links} service links, {Uses} resource uses",
                    to, resources.Count, links.Count, uses.Count);
            }

            if (totals.Services + totals.Instances + totals.Resources + totals.Dependencies > 0)
            {
                logger.LogInformation("Discovery created {Services} services, {Instances} instances, {Resources} resources, {Dependencies} dependencies",
                    totals.Services, totals.Instances, totals.Resources, totals.Dependencies);
            }
            return new DiscoveryRunResult(windows, watermark, totals);
        }

        // A window that fills the row limit may be missing rows: split it, down to the minimum window.
        private async Task<(List<ResourceObservation>, List<ServiceLinkObservation>, List<ResourceUseObservation>)> ReadAsync(
            string tenantKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        {
            var settings = options.Value;
            var window = new TelemetryWindow(tenantKey, from, to, settings.RowLimit);
            var resources = await source.ReadResourcesAsync(window, cancellationToken);
            var links = await source.ReadServiceLinksAsync(window, cancellationToken);
            var uses = await source.ReadResourceUsesAsync(window, cancellationToken);
            var full = resources.Count >= settings.RowLimit || links.Count >= settings.RowLimit || uses.Count >= settings.RowLimit;
            if (!full)
            {
                return ([.. resources], [.. links], [.. uses]);
            }
            if (to - from <= settings.MinWindow)
            {
                Truncated.Add(1, new KeyValuePair<string, object?>("kind", "window"));
                logger.LogWarning("Discovery window {From}..{To} still returns {Limit} rows at the minimum window; some resources are skipped",
                    from, to, settings.RowLimit);
                return ([.. resources], [.. links], [.. uses]);
            }
            var middle = from + (to - from) / 2;
            var first = await ReadAsync(tenantKey, from, middle, cancellationToken);
            var second = await ReadAsync(tenantKey, middle, to, cancellationToken);
            return ([.. first.Item1, .. second.Item1], [.. first.Item2, .. second.Item2], [.. first.Item3, .. second.Item3]);
        }

        private static void Record(ReconcileResult result, int truncatedValues)
        {
            Created.Add(result.Services, new KeyValuePair<string, object?>("kind", "service"));
            Created.Add(result.Instances, new KeyValuePair<string, object?>("kind", "instance"));
            Created.Add(result.Resources, new KeyValuePair<string, object?>("kind", "resource"));
            Created.Add(result.Dependencies, new KeyValuePair<string, object?>("kind", "dependency"));
            Truncated.Add(truncatedValues, new KeyValuePair<string, object?>("kind", "value"));
        }

        private static ReconcileResult Add(ReconcileResult a, ReconcileResult b)
            => new(a.Services + b.Services, a.Instances + b.Instances, a.Resources + b.Resources,
                a.Dependencies + b.Dependencies, a.Deactivated + b.Deactivated, a.Pruned + b.Pruned);

        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    }

    /// <summary>Runs <see cref="DiscoveryRunner"/> every <see cref="DiscoveryOptions.Interval"/>. A failed pass is logged and retried next time.</summary>
    public sealed class DiscoveryWorker(DiscoveryRunner runner, IOptions<DiscoveryOptions> options, ILogger<DiscoveryWorker> logger)
        : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(options.Value.Interval);
            do
            {
                try
                {
                    await runner.RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Discovery pass failed; retrying in {Interval}", options.Value.Interval);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
    }

    public static class DiscoveryServiceCollectionExtensions
    {
        /// <summary>Discovery from ClickHouse; the worker runs when <c>Discovery:Enabled</c> is true.</summary>
        public static IServiceCollection AddPlatformDiscovery(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<DiscoveryOptions>(configuration.GetSection(DiscoveryOptions.Section));
            services.Configure<ClickHouseOptions>(configuration.GetSection(ClickHouseOptions.Section));
            services.Configure<PlatformOptions>(configuration.GetSection(PlatformOptions.Section));
            services.AddSingleton<ClickHouseTelemetrySource>();
            services.AddSingleton<ITelemetrySource>(sp => sp.GetRequiredService<ClickHouseTelemetrySource>());
            services.AddSingleton<DiscoveryRunner>();
            services.AddSingleton(TimeProvider.System);
            if (configuration.GetSection(DiscoveryOptions.Section).GetValue(nameof(DiscoveryOptions.Enabled), true))
            {
                services.AddHostedService<DiscoveryWorker>();
            }
            return services;
        }
    }
}
