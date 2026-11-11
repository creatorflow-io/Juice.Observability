using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Juice.MediatR;
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
    /// stacked; the delay between due time and start is recorded as evaluator lag. Rules run in one replica at a time
    /// (<see cref="WorkerLease"/>).
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
            "{evaluation}", "Due evaluations not run, by reason (overrun: still running or waiting when due again; released: the lease went to another replica while it waited; disabled: turned off while it waited)");
        private static readonly Counter<long> Queued = RuleEvaluator.Meter.CreateCounter<long>("juice.observability.rules.queued",
            "{evaluation}", "Due evaluations that waited for a free worker before running; the wait is in rules.lag");

        private readonly ConcurrentDictionary<Guid, DateTimeOffset> _due = new();
        private readonly ConcurrentDictionary<Guid, Task> _running = new();
        /// <summary>
        /// The workers. Every due rule is started on its tick and waits here for one, so a worker that frees up starts the next rule at once. Waiting
        /// for the next tick instead capped the scheduler at MaxConcurrency rules per tick (48 a minute at the defaults), however quick each was.
        /// </summary>
        private readonly SemaphoreSlim _workers = new(Math.Max(1, options.Value.MaxConcurrency));
        /// <summary>Cancelled when this replica loses the lease, so rules it queued don't run beside the replica that took over.</summary>
        private CancellationTokenSource _holding = new();
        private int _inFlight;

        /// <summary>The most evaluations that ran at once; tests check it against MaxConcurrency.</summary>
        internal int PeakInFlight { get; private set; }

        /// <summary>The enabled rules as last read, shared by the evaluations that waited for a worker: read again at most once a second.</summary>
        private (DateTimeOffset At, Task<Dictionary<Guid, Rule>> Rules)? _current;
        private readonly Lock _currentLock = new();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await using var lease = WorkerLease.For(scopes, "rules", logger);
            var held = false;
            var generation = 0;
            using var timer = new PeriodicTimer(options.Value.Tick);
            do
            {
                try
                {
                    var holds = await lease.TryHoldAsync(stoppingToken);
                    if (!holds && held)
                    {
                        // Lost to another replica: rules still waiting for a worker give up, it evaluates them now.
                        await _holding.CancelAsync();
                    }
                    else if (holds && lease.Generation != generation)
                    {
                        // Taken (back), even within one call: another replica may have held it meanwhile. What this replica queued before
                        // gives up, and due times from before are stale: every rule is due now.
                        generation = lease.Generation;
                        await _holding.CancelAsync();
                        var previous = _holding;
                        _holding = new CancellationTokenSource();
                        previous.Dispose();
                        _due.Clear();
                    }
                    held = holds;
                    if (held)
                    {
                        await TickAsync(stoppingToken);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Rule scheduling failed; retrying next tick");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
            await Task.WhenAll(_running.Values);
        }

        /// <summary>Starts every due rule; each runs when a worker is free. Exposed for tests.</summary>
        public async Task TickAsync(CancellationToken cancellationToken)
        {
            // Before reading the rules: a rule that waits for a worker is checked against a list read after this, never one from before the tick.
            var tickAt = clock.GetUtcNow();
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var tenantId = await db.Tenants.Where(t => t.Key == platform.Value.TenantKey).Select(t => t.Id).SingleAsync(cancellationToken);
            // SLO burn rules have no query to run: their alerts come from the SLO counter's burn rates.
            var rules = await db.Rules.AsNoTracking().Where(r => r.TenantId == tenantId && r.Enabled && r.Template != RuleTemplate.SloBurn).ToListAsync(cancellationToken);
            var now = clock.GetUtcNow();
            var hold = _holding.Token;
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
                _due[rule.Id] = Next(due, schedule, now);
                // Registered before it starts, so its own removal on completion can never come first.
                var start = new Task<Task>(() => RunAsync(rule, due, tickAt, hold, cancellationToken));
                _running[rule.Id] = start.Unwrap();
                start.Start(TaskScheduler.Default);
            }
        }

        /// <summary>Evaluations in flight; tests wait on them.</summary>
        public Task Running => Task.WhenAll(_running.Values);

        /// <summary>
        /// Waits for a worker, then evaluates. It gives up if this replica lost the lease meanwhile (the replica that took it evaluates it now). A
        /// rule that had to wait runs as the rule is now, not as it was on its tick: one disabled or changed while it waited is skipped or evaluated
        /// as changed.
        /// </summary>
        private async Task RunAsync(Rule rule, DateTimeOffset due, DateTimeOffset tickAt, CancellationToken hold, CancellationToken stoppingToken)
        {
            var queued = false;
            try
            {
                if (!_workers.Wait(0))
                {
                    queued = true;
                    Queued.Add(1, new KeyValuePair<string, object?>("template", rule.Template.ToString()));
                    using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, hold);
                    await _workers.WaitAsync(waiting.Token);
                }
            }
            catch (OperationCanceledException)
            {
                if (!stoppingToken.IsCancellationRequested) Skipped.Add(1, new KeyValuePair<string, object?>("reason", "released"));
                _running.TryRemove(rule.Id, out _);
                return;
            }
            var inFlight = Interlocked.Increment(ref _inFlight);
            lock (_currentLock) PeakInFlight = Math.Max(PeakInFlight, inFlight);
            var outcome = "success";
            var started = Stopwatch.GetTimestamp();
            // Counted as an evaluation unless it gives up below: a failure to read the rule again is a failed evaluation, not a silent one.
            var counted = true;
            try
            {
                // Waited or not: a task can start after the lease was lost even when a worker is free.
                if (hold.IsCancellationRequested)
                {
                    counted = false;
                    Skipped.Add(1, new KeyValuePair<string, object?>("reason", "released"));
                    return;
                }
                if (queued)
                {
                    if (!(await CurrentAsync(tickAt, stoppingToken)).TryGetValue(rule.Id, out var current))
                    {
                        counted = false;
                        Skipped.Add(1, new KeyValuePair<string, object?>("reason", "disabled"));
                        return;
                    }
                    rule = current;
                }
                // The wait for a worker shows as lag; the timeout and duration count from the start.
                Lag.Record((clock.GetUtcNow() - due).TotalSeconds, new KeyValuePair<string, object?>("template", rule.Template.ToString()),
                    new KeyValuePair<string, object?>("queued", queued));
                started = Stopwatch.GetTimestamp();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(options.Value.EvaluationTimeout);
                await evaluator.EvaluateAsync(rule, timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down: nothing was evaluated to count.
                counted = false;
            }
            catch (OperationCanceledException)
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
                if (counted)
                {
                    Evaluations.Add(1, new KeyValuePair<string, object?>("template", rule.Template.ToString()),
                        new KeyValuePair<string, object?>("outcome", outcome));
                    Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
                }
                Interlocked.Decrement(ref _inFlight);
                _workers.Release();
                _running.TryRemove(rule.Id, out _);
            }
        }

        /// <summary>
        /// The rules the scheduler runs, as they are now (gone, disabled and SLO burn rules left out), by id. Read once and shared for a second, so
        /// the evaluations that waited for a worker on a tick don't each query; but never a list read before <paramref name="notBefore"/>, the tick
        /// that queued the caller, or a rule enabled or changed just before that tick would look gone or old.
        /// </summary>
        private Task<Dictionary<Guid, Rule>> CurrentAsync(DateTimeOffset notBefore, CancellationToken ct)
        {
            lock (_currentLock)
            {
                var now = clock.GetUtcNow();
                if (_current is { } cached && cached.At >= notBefore && now - cached.At < TimeSpan.FromSeconds(1) && !cached.Rules.IsFaulted && !cached.Rules.IsCanceled)
                {
                    return cached.Rules;
                }
                var read = ReadAsync();
                _current = (now, read);
                return read;
            }

            async Task<Dictionary<Guid, Rule>> ReadAsync()
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                return await db.Rules.AsNoTracking().Where(r => r.Enabled && r.Template != RuleTemplate.SloBurn).ToDictionaryAsync(r => r.Id, ct);
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
    /// rule). Each change writes an <see cref="AuditRecord"/> in the same transaction. The file owns what it lists, taking over a key the API
    /// created; a rule it no longer lists is disabled and handed to the API; a rule the API created and the file never listed is left alone.
    /// </summary>
    public sealed class RuleProvisioner(IServiceScopeFactory scopes, IOptions<RulesOptions> options, ILogger<RuleProvisioner> logger) : IHostedService
    {
        public const string Actor = "deployment-config";
        public const string ClosedRuleDisabled = "rule_disabled";
        public const string ClosedRuleChanged = "rule_changed";

        /// <summary>A change after which an open alert's subject may never be observed again, so nothing could resolve it.</summary>
        public static bool ChangesWhatItWatches(RuleDefinition before, RuleDefinition after)
            => before.Template != after.Template || before.Service != after.Service || before.Environment != after.Environment || before.Host != after.Host;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var definitions = options.Value.Definitions;
            var errors = definitions.SelectMany(d => d.Validate()).ToList();
            errors.AddRange(definitions.GroupBy(d => d.Key).Where(g => g.Count() > 1).Select(g => $"rule '{g.Key}': defined more than once"));
            if (errors.Count > 0)
            {
                throw new InvalidOperationException("Invalid rule configuration:\n" + string.Join("\n", errors));
            }

            using var context = PlatformMessageContext.Begin("rule-provisioning");
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            // Replicas starting together load the file one after another: the second sees the first's rules, not a conflict. A session lock on the
            // connection the command's transaction will use, since the handler runs before the TransactionBehavior begins it.
            var key = WorkerLease.KeyOf("rule-provisioning");
            await db.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({key})", cancellationToken);
                try
                {
                    var changes = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new ProvisionRules(definitions), cancellationToken);
                    logger.LogInformation("Rules loaded: {Count} configured, {Changes} changed", definitions.Count, changes);
                }
                finally
                {
                    await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({key})", CancellationToken.None);
                }
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Make the rules the deployment's file lists match it. Returns how many changed.</summary>
    public sealed record ProvisionRules(IReadOnlyList<RuleDefinition> Definitions) : MessageBase, IRequest<int>;

    /// <summary>
    /// Creates, updates and hands over rules as the file says, with an audit record and change event for each, and resolves the open alerts of a rule
    /// that no longer watches them. Only changes entities: the TransactionBehavior saves them, and the notifications those alerts raise, together.
    /// </summary>
    public sealed class ProvisionRulesHandler(PlatformDbContext db, IOptions<PlatformOptions> platform, TimeProvider clock) : IRequestHandler<ProvisionRules, int>
    {
        private const string Actor = RuleProvisioner.Actor;
        private const string ClosedRuleDisabled = RuleProvisioner.ClosedRuleDisabled;
        private const string ClosedRuleChanged = RuleProvisioner.ClosedRuleChanged;

        private static bool ChangesWhatItWatches(RuleDefinition before, RuleDefinition after) => RuleProvisioner.ChangesWhatItWatches(before, after);

        public async ValueTask<int> Handle(ProvisionRules request, CancellationToken cancellationToken)
        {
            var definitions = request.Definitions;
            var tenantId = await db.Tenants.Where(t => t.Key == platform.Value.TenantKey).Select(t => t.Id).SingleAsync(cancellationToken);
            var existing = await db.Rules.Where(r => r.TenantId == tenantId).ToDictionaryAsync(r => r.Key, cancellationToken);
            var now = clock.GetUtcNow();
            var changes = 0;
            void Audit(string operation, Rule rule)
            {
                db.AuditRecords.Add(new AuditRecord
                {
                    TenantId = tenantId, Actor = Actor, Operation = operation, TargetType = "rule", TargetId = rule.Key,
                    Outcome = AuditOutcome.Succeeded, Metadata = new() { ["hash"] = rule.DefinitionHash, ["template"] = rule.Template.ToString() },
                });
                db.ChangeEvents.Add(new ChangeEvent
                {
                    TenantId = tenantId, Kind = ChangeKind.Configuration, Source = ChangeEvent.PlatformSource, Actor = Actor,
                    Summary = $"Rule {rule.Key}: {operation.Replace("rule.", "")}", Attributes = new() { [ChangeEvent.RuleAttribute] = rule.Key },
                    OccurredAt = now, RecordedAt = now,
                });
            }

            // The file can't make a rule with an SLO burn rule's key, or take one over; a rule that held the key before it was reserved keeps it.
            var reserved = definitions.Where(d => RuleDefinition.IsReservedKey(d.Key)
                && (!existing.TryGetValue(d.Key, out var holder) || holder.ManagedBy == ManagedBy.Platform)).Select(d => RuleDefinition.ReservedKeyError(d.Key)).ToList();
            if (reserved.Count > 0)
            {
                throw new InvalidOperationException("Invalid rule configuration:\n" + string.Join("\n", reserved));
            }
            foreach (var definition in definitions)
            {
                var hash = definition.Hash();
                if (!existing.TryGetValue(definition.Key, out var rule))
                {
                    rule = new Rule
                    {
                        TenantId = tenantId, Key = definition.Key, Name = definition.Name, Template = definition.Template,
                        Severity = definition.Severity, Definition = definition.ToJson(), DefinitionHash = hash,
                        Enabled = definition.Enabled, LoadedAt = now, ManagedBy = ManagedBy.Configuration,
                    };
                    db.Rules.Add(rule);
                    Audit("rule.create", rule);
                    changes++;
                }
                else if (rule.DefinitionHash != hash || rule.ManagedBy != ManagedBy.Configuration)
                {
                    rule.ManagedBy = ManagedBy.Configuration;
                    if (ChangesWhatItWatches(RuleDefinition.FromJson(rule.Definition), definition) || !definition.Enabled)
                    {
                        await EvaluateRuleHandler.CloseOpenAlertsAsync(db, rule, definition.Enabled ? ClosedRuleChanged : ClosedRuleDisabled, now, cancellationToken);
                    }
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
            foreach (var removed in existing.Values.Where(r => r.ManagedBy == ManagedBy.Configuration && !configured.Contains(r.Key)))
            {
                removed.ManagedBy = ManagedBy.Api;
                removed.LoadedAt = now;
                // Whether or not it's enabled: an earlier version disabled rules without closing their alerts, and those would fire forever.
                await EvaluateRuleHandler.CloseOpenAlertsAsync(db, removed, ClosedRuleDisabled, now, cancellationToken);
                if (removed.Enabled)
                {
                    removed.Enabled = false;
                    Audit("rule.disable", removed);
                    changes++;
                }
            }
            return changes;
        }
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
