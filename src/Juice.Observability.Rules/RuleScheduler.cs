using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Rules
{
    /// <summary>
    /// Runs every enabled rule on its schedule with bounded workers (requirements §8, roadmap Phase 3): at most
    /// <see cref="RulesOptions.MaxConcurrency"/> evaluations at once, each cancelled after
    /// <see cref="RulesOptions.EvaluationTimeout"/>. A rule still running when it is due again is skipped, never
    /// stacked; the delay between due time and start is recorded as evaluator lag.
    /// </summary>
    public sealed class RuleScheduler(IServiceScopeFactory scopes, RuleEvaluator evaluator, IOptions<RulesOptions> options,
        IOptions<PlatformOptions> platform, TimeProvider clock, ILogger<RuleScheduler> logger) : BackgroundService
    {
        private static readonly Counter<long> Evaluations = RuleEvaluator.Meter.CreateCounter<long>("juice.observability.rules.evaluations",
            "{evaluation}", "Rule evaluations, by template and outcome (success, failure, timeout)");
        private static readonly Histogram<double> Duration = RuleEvaluator.Meter.CreateHistogram<double>("juice.observability.rules.duration",
            "s", "Duration of one rule evaluation");
        private static readonly Histogram<double> Lag = RuleEvaluator.Meter.CreateHistogram<double>("juice.observability.rules.lag",
            "s", "Delay between a rule's due time and the start of its evaluation");
        private static readonly Counter<long> Skipped = RuleEvaluator.Meter.CreateCounter<long>("juice.observability.rules.skipped",
            "{evaluation}", "Due evaluations not started, by reason (overrun: still running; saturated: no free worker)");

        private readonly ConcurrentDictionary<Guid, DateTimeOffset> _due = new();
        private readonly ConcurrentDictionary<Guid, Task> _running = new();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(options.Value.Tick);
            do
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Rule scheduling failed; retrying next tick");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
            await Task.WhenAll(_running.Values);
        }

        /// <summary>Starts every due rule that has a free worker. Exposed for tests.</summary>
        public async Task TickAsync(CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenantId = await db.Tenants.Where(t => t.Key == platform.Value.TenantKey).Select(t => t.Id).SingleAsync(cancellationToken);
            var rules = await db.Rules.AsNoTracking().Where(r => r.TenantId == tenantId && r.Enabled).ToListAsync(cancellationToken);
            var now = clock.GetUtcNow();
            foreach (var rule in rules)
            {
                var schedule = RuleDefinition.FromJson(rule.Definition).Schedule;
                var due = _due.GetOrAdd(rule.Id, now);
                if (now < due)
                {
                    continue;
                }
                if (_running.ContainsKey(rule.Id))
                {
                    Skipped.Add(1, new KeyValuePair<string, object?>("reason", "overrun"));
                    _due[rule.Id] = Next(due, schedule, now);
                    continue;
                }
                if (_running.Count >= options.Value.MaxConcurrency)
                {
                    Skipped.Add(1, new KeyValuePair<string, object?>("reason", "saturated"));
                    continue; // stays due: it starts on a later tick, and the wait shows as lag
                }
                Lag.Record((now - due).TotalSeconds, new KeyValuePair<string, object?>("template", rule.Template.ToString()));
                _due[rule.Id] = Next(due, schedule, now);
                // Registered before it starts, so its own removal on completion can never come first.
                var start = new Task<Task>(() => RunAsync(rule, cancellationToken));
                _running[rule.Id] = start.Unwrap();
                start.Start(TaskScheduler.Default);
            }
        }

        /// <summary>Evaluations in flight; tests wait on them.</summary>
        public Task Running => Task.WhenAll(_running.Values);

        private async Task RunAsync(Rule rule, CancellationToken stoppingToken)
        {
            var started = Stopwatch.GetTimestamp();
            var outcome = "success";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(options.Value.EvaluationTimeout);
            try
            {
                await evaluator.EvaluateAsync(rule, timeout.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                outcome = "timeout";
                logger.LogWarning("Rule {Rule} timed out after {Timeout}", rule.Key, options.Value.EvaluationTimeout);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                outcome = "failure";
                logger.LogError(ex, "Rule {Rule} evaluation failed; alerts are unchanged", rule.Key);
            }
            finally
            {
                Evaluations.Add(1, new KeyValuePair<string, object?>("template", rule.Template.ToString()),
                    new KeyValuePair<string, object?>("outcome", outcome));
                Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
                _running.TryRemove(rule.Id, out _);
            }
        }

        // The next due time on the rule's grid; missed slots are skipped rather than run back to back.
        private static DateTimeOffset Next(DateTimeOffset due, TimeSpan schedule, DateTimeOffset now)
        {
            var next = due + schedule;
            if (next <= now)
            {
                var missed = (long)Math.Ceiling((now - next) / schedule);
                if (missed > 0) Skipped.Add(missed, new KeyValuePair<string, object?>("reason", "overrun"));
                next += schedule * Math.Max(missed, 1);
            }
            return next;
        }
    }

    /// <summary>
    /// Loads <c>Rules:Definitions</c> at startup (requirements §6.1): validates every rule (any error stops the host),
    /// creates new rules, updates changed ones and disables rules removed from configuration (alerts keep their
    /// rule). Each change writes an <see cref="AuditRecord"/> in the same transaction.
    /// </summary>
    public sealed class RuleProvisioner(IServiceScopeFactory scopes, IOptions<RulesOptions> options, IOptions<PlatformOptions> platform,
        TimeProvider clock, ILogger<RuleProvisioner> logger) : IHostedService
    {
        public const string Actor = "deployment-config";

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var definitions = options.Value.Definitions;
            var errors = definitions.SelectMany(d => d.Validate()).ToList();
            errors.AddRange(definitions.GroupBy(d => d.Key).Where(g => g.Count() > 1).Select(g => $"rule '{g.Key}': defined more than once"));
            if (errors.Count > 0)
            {
                throw new InvalidOperationException("Invalid rule configuration:\n" + string.Join("\n", errors));
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var tenantId = await db.Tenants.Where(t => t.Key == platform.Value.TenantKey).Select(t => t.Id).SingleAsync(cancellationToken);
            var existing = await db.Rules.Where(r => r.TenantId == tenantId).ToDictionaryAsync(r => r.Key, cancellationToken);
            var now = clock.GetUtcNow();
            var changes = 0;
            void Audit(string operation, Rule rule) => db.AuditRecords.Add(new AuditRecord
            {
                TenantId = tenantId, Actor = Actor, Operation = operation, TargetType = "rule", TargetId = rule.Key,
                Outcome = AuditOutcome.Succeeded, Metadata = new() { ["hash"] = rule.DefinitionHash, ["template"] = rule.Template.ToString() },
            });

            foreach (var definition in definitions)
            {
                var hash = definition.Hash();
                if (!existing.TryGetValue(definition.Key, out var rule))
                {
                    rule = new Rule
                    {
                        TenantId = tenantId, Key = definition.Key, Name = definition.Name, Template = definition.Template,
                        Severity = definition.Severity, Definition = definition.ToJson(), DefinitionHash = hash,
                        Enabled = definition.Enabled, LoadedAt = now,
                    };
                    db.Rules.Add(rule);
                    Audit("rule.create", rule);
                    changes++;
                }
                else if (rule.DefinitionHash != hash)
                {
                    rule.Name = definition.Name;
                    rule.Template = definition.Template;
                    rule.Severity = definition.Severity;
                    rule.Definition = definition.ToJson();
                    rule.DefinitionHash = hash;
                    rule.Enabled = definition.Enabled;
                    rule.LoadedAt = now;
                    Audit("rule.update", rule);
                    changes++;
                }
            }
            var configured = definitions.Select(d => d.Key).ToHashSet();
            foreach (var removed in existing.Values.Where(r => r.Enabled && !configured.Contains(r.Key)))
            {
                removed.Enabled = false;
                removed.LoadedAt = now;
                Audit("rule.disable", removed);
                changes++;
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Rules loaded: {Count} configured, {Changes} changed", definitions.Count, changes);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public static class RulesServiceCollectionExtensions
    {
        /// <summary>
        /// Rule provisioning (always: the rules API reads what it loads) and, when <c>Rules:Enabled</c>, the scheduler.
        /// Register after the tenant provisioner.
        /// </summary>
        public static IServiceCollection AddPlatformRules(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<RulesOptions>(configuration.GetSection(RulesOptions.Section));
            services.AddSingleton<RuleTemplates>();
            services.AddSingleton<RuleEvaluator>();
            services.AddHostedService<RuleProvisioner>();
            if (configuration.GetSection(RulesOptions.Section).GetValue(nameof(RulesOptions.Enabled), true))
            {
                services.AddSingleton<RuleScheduler>();
                services.AddHostedService(sp => sp.GetRequiredService<RuleScheduler>());
            }
            return services;
        }
    }
}
