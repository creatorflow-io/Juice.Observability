using System.Diagnostics.Metrics;
using Juice.Observability.Api.Integrations;
using Juice.Observability.Api.Integrations.Health;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Juice.Observability.Tests.Integrations.Sample;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests.Integrations.Health
{
    /// <summary>Runs, races, missing plugins and metrics of the integrations worker, a pass at a time (decisions 6, 7 and 9). Local stack.</summary>
    [Trait("Milestone", "INT-I1")]
    public sealed class IntegrationRunTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static Dictionary<string, string?> Settings(string? runTimeout = null, int? failedAfter = null)
        {
            var settings = new Dictionary<string, string?>();
            if (runTimeout is not null) settings["Modules:integrations:RunTimeout"] = runTimeout;
            if (failedAfter is not null) settings["Modules:integrations:FailedAfter"] = failedAfter.Value.ToString();
            return settings;
        }

        // ─── Runs ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task Enabled_instance_runs_on_interval_and_records_success_and_checkpoint()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            var notDue = await host.AddAsync(name: "later", nextRunAt: DateTimeOffset.UtcNow.AddHours(1));

            Assert.Equal(LeasedPassOutcome.Succeeded, await host.PassAsync());

            var run = await host.GetAsync(id);
            Assert.Equal((IntegrationState.Enabled, 0, "Succeeded", null), (run.State, run.ConsecutiveFailures, run.LastOutcome, run.LastFailure));
            Assert.NotNull(run.LastSuccessAt);
            Assert.Null(run.RunToken);
            // The next run is an interval (5 minutes) away, give or take the 10 % jitter.
            Assert.InRange(run.NextRunAt!.Value, DateTimeOffset.UtcNow.AddMinutes(4.4), DateTimeOffset.UtcNow.AddMinutes(5.6));
            var checkpoint = Assert.Single(await host.CheckpointsAsync(id));
            Assert.Equal(("jobs", "cursor-1"), (checkpoint.Stream, checkpoint.Cursor));
            // The instance not yet due was left alone.
            Assert.Equal(1, host.Control.Collections);
            Assert.Null((await host.GetAsync(notDue)).LastRunAt);
        }

        [Fact]
        public async Task Failure_sequence_degraded_then_failed_then_recovers_enabled()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            host.Control.Result = CollectionResult.Failed;
            host.Control.Failure = IntegrationFailure.Remote;

            await host.PassAsync();
            Assert.Equal((IntegrationState.Degraded, 1), Shape(await host.GetAsync(id)));
            await host.MakeDueAsync(id);
            await host.PassAsync();
            Assert.Equal((IntegrationState.Degraded, 2), Shape(await host.GetAsync(id)));
            await host.MakeDueAsync(id);
            await host.PassAsync();
            var failed = await host.GetAsync(id);
            Assert.Equal((IntegrationState.Failed, 3), Shape(failed));
            Assert.Equal("Remote", failed.LastFailure);
            // A Failed instance is retried at the backoff cap, so it recovers by itself.
            Assert.InRange(failed.NextRunAt!.Value, DateTimeOffset.UtcNow.AddMinutes(53), DateTimeOffset.UtcNow.AddMinutes(67));
            // No success was ever recorded.
            Assert.Null(failed.LastSuccessAt);

            host.Control.Result = CollectionResult.Succeeded;
            host.Control.Failure = IntegrationFailure.None;
            await host.MakeDueAsync(id);
            await host.PassAsync();
            var recovered = await host.GetAsync(id);
            Assert.Equal((IntegrationState.Enabled, 0), Shape(recovered));
            Assert.Null(recovered.LastFailure);
            Assert.NotNull(recovered.LastSuccessAt);
        }

        private static (IntegrationState, int) Shape(IntegrationInstance i) => (i.State, i.ConsecutiveFailures);

        [Fact]
        public async Task Authentication_failure_is_failed_at_once_and_partial_is_degraded()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            host.Control.Result = CollectionResult.Failed;
            host.Control.Failure = IntegrationFailure.Authentication;
            await host.PassAsync();
            Assert.Equal((IntegrationState.Failed, 1), Shape(await host.GetAsync(id)));

            host.Control.Result = CollectionResult.Partial;
            host.Control.Failure = IntegrationFailure.RateLimited;
            host.Control.RetryAfter = TimeSpan.FromMinutes(30);
            await host.MakeDueAsync(id);
            await host.PassAsync();
            var partial = await host.GetAsync(id);
            Assert.Equal((IntegrationState.Degraded, 0), Shape(partial));
            Assert.InRange(partial.NextRunAt!.Value, DateTimeOffset.UtcNow.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(32));
        }

        [Fact]
        public async Task Run_timeout_counts_failure_kind_timeout()
        {
            await using var host = await IntegrationHealthHost.StartAsync(Settings(runTimeout: "00:00:00.300"));
            var id = await host.AddAsync();
            host.Control.CollectDelay = TimeSpan.FromSeconds(30);

            await host.PassAsync();

            var run = await host.GetAsync(id);
            Assert.Equal((IntegrationState.Degraded, 1), Shape(run));
            Assert.Equal("Timeout", run.LastFailure);
            Assert.Empty(await host.CheckpointsAsync(id));
        }

        [Fact]
        public async Task Plugin_throws_records_internal_detail_scrubbed()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            host.Control.ThrowOnCollect = true;

            await host.PassAsync();

            var run = await host.GetAsync(id);
            Assert.Equal((IntegrationState.Degraded, 1), Shape(run));
            Assert.Equal("Internal", run.LastFailure);
            Assert.Contains("the jobs service refused ***", run.LastFailureDetail);
            Assert.DoesNotContain(IntegrationHealthHost.Canary, run.LastFailureDetail + run.StateReason);
        }

        [Fact]
        public async Task Plugin_detail_on_a_failed_outcome_is_scrubbed_and_capped()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            host.Control.Result = CollectionResult.Failed;
            host.Control.Failure = IntegrationFailure.Remote;

            await host.PassAsync();

            var run = await host.GetAsync(id);
            Assert.Equal("jobs answered for ***", run.LastFailureDetail);
            Assert.DoesNotContain(IntegrationHealthHost.Canary, run.LastFailureDetail + run.StateReason);
        }

        // ─── Races ────────────────────────────────────────────────────────────

        [Fact]
        public async Task Disable_during_run_keeps_disabled_and_drops_checkpoints()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            host.Control.CollectDelay = TimeSpan.FromSeconds(1);

            var pass = host.PassAsync();
            await WaitUntilAsync(async () => (await host.GetAsync(id)).RunToken is not null);
            await host.UpdateAsync(id, i => { i.State = IntegrationState.Disabled; i.Version++; });
            await pass;

            var run = await host.GetAsync(id);
            Assert.Equal(IntegrationState.Disabled, run.State);
            Assert.Null(run.LastOutcome);
            Assert.Empty(await host.CheckpointsAsync(id));
        }

        [Fact]
        public async Task Config_change_during_run_drops_outcome()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            host.Control.CollectDelay = TimeSpan.FromSeconds(1);

            var pass = host.PassAsync();
            await WaitUntilAsync(async () => (await host.GetAsync(id)).RunToken is not null);
            // What an admin's PUT does: back to Configured, with a new version.
            await host.UpdateAsync(id, i => { i.State = IntegrationState.Configured; i.Version++; i.Config = """{"baseUrl":"https://other.example"}"""; });
            await pass;

            var run = await host.GetAsync(id);
            Assert.Equal(IntegrationState.Configured, run.State);
            Assert.Null(run.LastOutcome);
            Assert.Empty(await host.CheckpointsAsync(id));
        }

        [Fact]
        public async Task Two_workers_lease_handover_instance_claimed_once()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            await using var scope = host.Services.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<IntegrationRunner>();
            var seenByBoth = await host.GetAsync(id);

            var first = await runner.ClaimAsync(seenByBoth, Ct);
            var second = await runner.ClaimAsync(seenByBoth, Ct);
            Assert.NotNull(first);
            Assert.Null(second);

            // The first worker's run outlives its claim: the claim expired, and another replica took the instance.
            await host.MakeDueAsync(id);
            var other = await runner.ClaimAsync(await host.GetAsync(id), Ct);
            Assert.NotNull(other);
            Assert.NotEqual(first, other);

            // The stale run's outcome is ignored: nothing is written, and its checkpoints with it.
            Assert.False(await runner.RunAsync(seenByBoth, first!.Value, CancellationToken.None));
            var held = await host.GetAsync(id);
            Assert.Equal(other, held.RunToken);
            Assert.Null(held.LastOutcome);
            Assert.Empty(await host.CheckpointsAsync(id));
            // The current one is accepted.
            Assert.True(await runner.RunAsync(held, other!.Value, CancellationToken.None));
            Assert.Equal("Succeeded", (await host.GetAsync(id)).LastOutcome);
        }

        [Fact]
        public async Task Lease_lost_cancels_runs_no_outcome_written()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            host.Control.CollectDelay = TimeSpan.FromSeconds(30);
            using var lost = new CancellationTokenSource();

            var pass = host.PassAsync(lost.Token);
            await WaitUntilAsync(async () => (await host.GetAsync(id)).RunToken is not null);
            await lost.CancelAsync();

            await pass;
            var run = await host.GetAsync(id);
            Assert.Null(run.LastOutcome);
            Assert.Equal(IntegrationState.Enabled, run.State);
            Assert.Empty(await host.CheckpointsAsync(id));
        }

        // ─── Missing plugin, dependencies ─────────────────────────────────────

        [Fact]
        public async Task Plugin_removed_instance_failed_not_run_alert_fires()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            // Not due for an hour: a removed plugin is noticed at once, not at the next run.
            var id = await host.AddAsync(plugin: "gone.plugin", nextRunAt: DateTimeOffset.UtcNow.AddHours(1));

            await host.PassAsync();

            var run = await host.GetAsync(id);
            Assert.Equal(IntegrationState.Failed, run.State);
            Assert.Contains("not installed", run.StateReason);
            Assert.Equal(0, host.Control.Collections);
            var alert = Assert.Single(await host.AlertsAsync());
            Assert.Equal((AlertState.Firing, HealthSubject.Integration, IntegrationHealthRule.SubjectKey(id)), (alert.State, alert.SubjectType, alert.SubjectKey));

            // A second pass changes nothing: not rewritten, not fired again.
            var again = await host.GetAsync(id);
            await host.PassAsync();
            Assert.Equal(again.NextRunAt, (await host.GetAsync(id)).NextRunAt);
            Assert.Single(await host.AlertsAsync());
        }

        [Fact]
        public async Task Config_version_mismatch_is_failed_not_run()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync(configVersion: 7);

            await host.PassAsync();

            var run = await host.GetAsync(id);
            Assert.Equal(IntegrationState.Failed, run.State);
            Assert.Equal("Config version 7, plugin expects 1", run.StateReason);
            Assert.Equal(0, host.Control.Collections);
        }

        [Fact]
        public async Task Database_down_pass_fails_and_retries_no_partial_state()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            await using (var db = host.Platform())
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE integrations.integration_instances RENAME TO integration_instances_away", Ct);
            }

            await Assert.ThrowsAnyAsync<Exception>(() => host.PassAsync());
            Assert.Equal(0, host.Control.Collections);

            await using (var db = host.Platform())
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE integrations.integration_instances_away RENAME TO integration_instances", Ct);
            }
            Assert.Null((await host.GetAsync(id)).LastRunAt);
            Assert.Equal(LeasedPassOutcome.Succeeded, await host.PassAsync());
            Assert.Equal("Succeeded", (await host.GetAsync(id)).LastOutcome);
        }

        // ─── Idle ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task No_plugin_pass_idle_no_alert_no_rule()
        {
            await using var host = await IntegrationHealthHost.StartAsync(sample: false);
            var rules = await host.RuleCountAsync();

            Assert.Equal(LeasedPassOutcome.Idle, await host.PassAsync());
            Assert.Equal(LeasedPassOutcome.Idle, await host.PassAsync());

            Assert.Equal(rules, await host.RuleCountAsync());
            Assert.Empty(await host.AlertsAsync());
        }

        // ─── Metrics ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Metrics_runs_failures_freshness_tagged_by_id_not_name()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync(name: "secret-looking-name");
            var instance = await host.GetAsync(id);
            var measurements = new List<(string Name, double Value, Dictionary<string, object?> Tags)>();
            using var listener = Listen(measurements);

            await host.PassAsync();
            host.Control.Result = CollectionResult.Failed;
            host.Control.Failure = IntegrationFailure.Remote;
            await host.MakeDueAsync(id);
            await host.PassAsync();
            listener.RecordObservableInstruments();

            var n = id.ToString("N");
            var mine = measurements.Where(m => (string?)m.Tags.GetValueOrDefault("instance") == n).ToList();
            Assert.Contains(mine, m => m.Name == "juice.integration.runs" && (string?)m.Tags["outcome"] == "succeeded" && (string?)m.Tags["plugin"] == SampleJobsPlugin.Key);
            Assert.Contains(mine, m => m.Name == "juice.integration.runs" && (string?)m.Tags["outcome"] == "failed");
            Assert.Contains(mine, m => m.Name == "juice.integration.failures" && (string?)m.Tags["reason"] == "remote");
            var freshness = Assert.Single(mine, m => m.Name == "juice.integration.freshness");
            Assert.InRange(freshness.Value, 0, 60);
            // The name is nowhere in a label.
            Assert.DoesNotContain(measurements, m => m.Tags.Values.Any(v => v is string s && s.Contains(instance.Name, StringComparison.Ordinal)));
        }

        [Fact]
        public async Task Failures_counter_counts_failed_runs_only_and_instances_the_core_fails_are_counted()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            var gone = await host.AddAsync(plugin: "gone.plugin", name: "gone");
            var measurements = new List<(string Name, double Value, Dictionary<string, object?> Tags)>();
            using var listener = Listen(measurements);

            // A partial run that names what limited it is a run, and not a failure.
            host.Control.Result = CollectionResult.Partial;
            host.Control.Failure = IntegrationFailure.RateLimited;
            await host.PassAsync();

            var mine = measurements.Where(m => (string?)m.Tags.GetValueOrDefault("instance") == id.ToString("N")).ToList();
            Assert.Contains(mine, m => m.Name == "juice.integration.runs" && (string?)m.Tags["outcome"] == "partial");
            Assert.DoesNotContain(mine, m => m.Name == "juice.integration.failures");
            // An instance the core fails (its plugin isn't installed) is a failed run with a configuration failure, once.
            var missing = measurements.Where(m => (string?)m.Tags.GetValueOrDefault("instance") == gone.ToString("N")).ToList();
            Assert.Single(missing, m => m.Name == "juice.integration.runs" && (string?)m.Tags["outcome"] == "failed" && (string?)m.Tags["plugin"] == "gone.plugin");
            Assert.Single(missing, m => m.Name == "juice.integration.failures" && (string?)m.Tags["reason"] == "configuration");

            // And so is one whose config version isn't the plugin's.
            var stale = await host.AddAsync(name: "stale", configVersion: 9);
            await host.PassAsync();
            var versioned = measurements.Where(m => (string?)m.Tags.GetValueOrDefault("instance") == stale.ToString("N")).ToList();
            Assert.Single(versioned, m => m.Name == "juice.integration.failures" && (string?)m.Tags["reason"] == "configuration");
            Assert.Single(versioned, m => m.Name == "juice.integration.runs");
        }

        [Fact]
        public async Task Freshness_reported_only_for_enabled_and_degraded_instances()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            var measurements = new List<(string Name, double Value, Dictionary<string, object?> Tags)>();
            using var listener = Listen(measurements);
            await host.PassAsync();
            listener.RecordObservableInstruments();
            Assert.Contains(measurements, m => m.Name == "juice.integration.freshness" && (string?)m.Tags["instance"] == id.ToString("N"));

            // Failed: it has a last success, but isn't collecting, so it stops being reported as a fresh one.
            host.Control.Result = CollectionResult.Failed;
            host.Control.Failure = IntegrationFailure.Authentication;
            await host.MakeDueAsync(id);
            await host.PassAsync();
            Assert.Equal(IntegrationState.Failed, (await host.GetAsync(id)).State);
            measurements.Clear();
            listener.RecordObservableInstruments();

            Assert.DoesNotContain(measurements, m => m.Name == "juice.integration.freshness" && (string?)m.Tags["instance"] == id.ToString("N"));
        }

        [Fact]
        public async Task Deleted_instance_freshness_pruned()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            var measurements = new List<(string Name, double Value, Dictionary<string, object?> Tags)>();
            using var listener = Listen(measurements);
            await host.PassAsync();
            listener.RecordObservableInstruments();
            Assert.Contains(measurements, m => m.Name == "juice.integration.freshness" && (string?)m.Tags["instance"] == id.ToString("N"));

            await using (var scope = host.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>().Instances.Where(i => i.Id == id).ExecuteDeleteAsync(Ct);
            }
            // Active instances are what keep a pass from idling: a deleted one leaves nothing, so a pass forgets it.
            await host.PassAsync();
            measurements.Clear();
            listener.RecordObservableInstruments();

            Assert.DoesNotContain(measurements, m => m.Name == "juice.integration.freshness" && (string?)m.Tags["instance"] == id.ToString("N"));
        }

        private static MeterListener Listen(List<(string Name, double Value, Dictionary<string, object?> Tags)> into)
        {
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Name.StartsWith("juice.integration.", StringComparison.Ordinal)) l.EnableMeasurementEvents(instrument);
                },
            };
            void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            {
                var map = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
                lock (into) into.Add((instrument.Name, value, map));
            }
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            listener.Start();
            return listener;
        }

        private static async Task WaitUntilAsync(Func<Task<bool>> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!await condition())
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("Not seen in time");
                await Task.Delay(25, Ct);
            }
        }
    }
}
