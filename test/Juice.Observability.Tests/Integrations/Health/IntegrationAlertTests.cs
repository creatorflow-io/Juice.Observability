using Juice.Observability.Api.Integrations;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Juice.Observability.Rules;
using Juice.Observability.Rules.Notifications;
using Juice.Observability.Tests.Integrations.Sample;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Juice.Observability.Tests.Integrations.Health
{
    /// <summary>The integration alert: the lazily ensured platform rule, one alert per failed instance, its resolution, and that nothing else of the core moves (decision 8, AC-43). Local stack.</summary>
    [Trait("Milestone", "INT-I1")]
    public sealed class IntegrationAlertTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static async Task FailAsync(IntegrationHealthHost host, Guid id, IntegrationFailure kind = IntegrationFailure.Authentication)
        {
            host.Control.Result = CollectionResult.Failed;
            host.Control.Failure = kind;
            await host.MakeDueAsync(id);
            await host.PassAsync();
        }

        [Fact]
        public async Task Failed_instance_fires_one_alert_resolves_on_recovery_disable_delete()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var recovering = await host.AddAsync(name: "recovering");
            var disabling = await host.AddAsync(name: "disabling");
            var deleting = await host.AddAsync(name: "deleting");
            host.Control.Result = CollectionResult.Failed;
            host.Control.Failure = IntegrationFailure.Authentication;

            await host.PassAsync();
            await host.PassAsync();

            var alerts = await host.AlertsAsync();
            Assert.Equal(3, alerts.Count);
            Assert.All(alerts, a => Assert.Equal((AlertState.Firing, HealthSubject.Integration, null, null, AlertSeverity.Warning),
                (a.State, a.SubjectType, a.SubjectId, a.EnvironmentId, a.Severity)));
            Assert.Equal(new[] { recovering, disabling, deleting }.Select(IntegrationHealthRule.DedupKey).Order(), alerts.Select(a => a.DedupKey).Order());

            // Recovered.
            host.Control.Result = CollectionResult.Succeeded;
            host.Control.Failure = IntegrationFailure.None;
            await host.MakeDueAsync(recovering);
            // Disabled: an admin's disable, which also stops its runs.
            await host.UpdateAsync(disabling, i => { i.State = IntegrationState.Disabled; i.Version++; });
            // Deleted.
            await using (var scope = host.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>().Instances.Where(i => i.Id == deleting).ExecuteDeleteAsync(Ct);
            }
            await host.PassAsync();
            await host.PassAsync();

            await using var db = host.Platform();
            var after = await host.AlertsAsync();
            Assert.Equal(3, after.Count);
            Assert.All(after, a => Assert.Equal(AlertState.Resolved, a.State));
            var reasons = await db.AlertTransitions.AsNoTracking().Where(t => t.ToState == AlertState.Resolved && after.Select(a => a.Id).Contains(t.AlertId)).ToListAsync(Ct);
            Assert.Equal(["deleted", "disabled", "recovered"], reasons.Select(t => t.Reason).Order());
        }

        [Fact]
        public async Task Rule_ensured_lazily_core_only_rule_list_unchanged()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var rules = await host.RuleCountAsync();
            var id = await host.AddAsync();

            // Healthy instances, runs and a degraded one: no alert, so no rule.
            await host.PassAsync();
            host.Control.Result = CollectionResult.Failed;
            host.Control.Failure = IntegrationFailure.Remote;
            await host.MakeDueAsync(id);
            await host.PassAsync();
            Assert.Equal(rules, await host.RuleCountAsync());

            await FailAsync(host, id);

            Assert.Equal(rules + 1, await host.RuleCountAsync());
            await using var db = host.Platform();
            var rule = await db.Rules.AsNoTracking().SingleAsync(r => r.Key == IntegrationHealthRule.Key, Ct);
            Assert.Equal((RuleTemplate.IntegrationHealth, AlertSeverity.Warning, ManagedBy.Platform, true), (rule.Template, rule.Severity, rule.ManagedBy, rule.Enabled));
            Assert.True(await db.AuditRecords.AnyAsync(a => a.Operation == "rule.create" && a.TargetId == IntegrationHealthRule.Key, Ct));
            Assert.True(await db.ChangeEvents.AnyAsync(c => c.Summary == "Rule integration-health: create", Ct));
            // Never a scheduled rule: the registry the scheduler works from does not hold the template.
            Assert.False(host.Services.GetRequiredService<IRuleTemplateRegistry>().TryGet("IntegrationHealth", out _));
        }

        [Fact]
        public async Task Non_platform_rule_holding_key_no_alert_logged_once()
        {
            var logs = new LogCapture();
            await using var host = await IntegrationHealthHost.StartAsync(logs: logs);
            await using (var db = host.Platform())
            {
                db.Rules.Add(new Rule
                {
                    TenantId = host.Host.TenantId, Key = IntegrationHealthRule.Key, Name = "Mine", Template = RuleTemplate.ErrorRate, Definition = "{}", DefinitionHash = "h", ManagedBy = ManagedBy.Api,
                });
                await db.SaveChangesAsync(Ct);
            }
            var id = await host.AddAsync();

            await FailAsync(host, id);
            await host.PassAsync();
            await host.PassAsync();

            Assert.Empty(await host.AlertsAsync());
            await using var check = host.Platform();
            Assert.Equal(ManagedBy.Api, (await check.Rules.AsNoTracking().SingleAsync(r => r.Key == IntegrationHealthRule.Key, Ct)).ManagedBy);
            Assert.Single(logs.At(LogLevel.Warning), e => e.Message.Contains("integration-health", StringComparison.Ordinal));
        }

        [Fact]
        public async Task Concurrent_reconcile_one_open_alert()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            await FailAsync(host, id);

            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.PassAsync()));

            var open = (await host.AlertsAsync()).Where(a => a.State != AlertState.Resolved).ToList();
            Assert.Single(open);
        }

        [Fact]
        public async Task Alert_evidence_has_no_plugin_detail()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync(name: "payroll");
            await FailAsync(host, id);

            var alert = Assert.Single(await host.AlertsAsync());

            Assert.Equal(["consecutiveFailures", "failure", "instance", "plugin"], alert.Evidence.Keys.Order());
            Assert.Equal((SampleJobsPlugin.Key, "Authentication", "1"), (alert.Evidence["plugin"], alert.Evidence["failure"], alert.Evidence["consecutiveFailures"]));
            Assert.StartsWith("payroll", alert.Evidence["instance"]);
            // The plugin's detail ("jobs answered for ***") is in the instance, and nowhere else.
            Assert.DoesNotContain(alert.Evidence.Values, v => v.Contains("jobs answered", StringComparison.Ordinal) || v.Contains(IntegrationHealthHost.Canary, StringComparison.Ordinal));
        }

        [Fact]
        public async Task Apply_integration_subject_with_subject_or_environment_id_refused()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            await FailAsync(host, id);
            var other = Guid.NewGuid();
            await using var scope = host.Services.CreateAsyncScope();
            var source = scope.ServiceProvider.GetRequiredKeyedService<IAlertSource>(AlertSourceKeys.Key("integrations", "IntegrationHealth"));
            AlertSignal Fire(AlertSubject subject) => new(IntegrationHealthRule.Key, IntegrationHealthRule.DedupKey(other), AlertSignalKind.Fire, DateTimeOffset.UtcNow, "x", subject);

            await Assert.ThrowsAsync<ArgumentException>(() => source.ApplyAsync([Fire(new(AlertSubjectKind.Integration, $"integration:{other:N}", SubjectId: Guid.NewGuid()))], Ct));
            await Assert.ThrowsAsync<ArgumentException>(() => source.ApplyAsync([Fire(new(AlertSubjectKind.Integration, $"integration:{other:N}", EnvironmentId: Guid.NewGuid()))], Ct));
            // The template raises only integration subjects.
            await Assert.ThrowsAsync<ArgumentException>(() => source.ApplyAsync([Fire(new(AlertSubjectKind.Service, "service:shop/orders@production"))], Ct));
            Assert.Single(await host.AlertsAsync());
        }

        [Fact]
        public async Task Integration_alert_routes_notifies_and_silences_by_subject_key()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            var id = await host.AddAsync();
            await using (var db = host.Platform())
            {
                var before = await db.Outbox.CountAsync(Ct);
                await FailAsync(host, id);
                // The alert's transitions reach the outbox, as every platform alert's do: that is what notifies.
                Assert.True(await db.Outbox.CountAsync(Ct) > before);
            }
            var alert = Assert.Single(await host.AlertsAsync());
            var context = new AlertContext(IntegrationHealthRule.Key, alert.SubjectKey, alert.Severity);
            var now = DateTimeOffset.UtcNow;
            Silence Make(string[] rules, string? service = null, bool all = false)
                => new() { Comment = "c", CreatedBy = "t", RuleKeys = rules, Service = service, AllAlerts = all, StartsAt = now.AddMinutes(-1), EndsAt = now.AddHours(1) };

            Assert.Equal("integration:" + id.ToString("N"), alert.SubjectKey);
            Assert.True(SilenceMatcher.Matches(Make([IntegrationHealthRule.Key]), context));
            Assert.True(SilenceMatcher.Matches(Make([], all: true), context));
            // A silence on a service or a host never holds an integration's alert.
            Assert.False(SilenceMatcher.Matches(Make([IntegrationHealthRule.Key], service: "orders"), context));
            Assert.False(SilenceMatcher.Matches(Make(["other-rule"]), context));
        }

        // ─── AC-43 ────────────────────────────────────────────────────────────

        [Fact]
        public async Task Failing_instance_leaves_service_and_host_health_and_rule_evaluation_identical()
        {
            await using var host = await IntegrationHealthHost.StartAsync();
            await using var db = host.Platform();
            async Task<(int Snapshots, int Transitions, int Rules, int OtherAlerts)> CountsAsync()
                => (await db.HealthSnapshots.CountAsync(Ct), await db.HealthTransitions.CountAsync(Ct),
                    await db.Rules.CountAsync(r => r.Template != RuleTemplate.IntegrationHealth, Ct),
                    await db.Alerts.CountAsync(a => a.SubjectType != HealthSubject.Integration, Ct));
            var before = await CountsAsync();
            var id = await host.AddAsync();

            await FailAsync(host, id);
            await FailAsync(host, id);

            Assert.Single(await host.AlertsAsync());
            Assert.Equal(before, await CountsAsync());
            Assert.Empty(await db.HealthSnapshots.Where(h => h.SubjectType == HealthSubject.Integration).ToListAsync(Ct));
            // Whatever the integration states, the rule scheduler's templates are the ten the registry holds.
            Assert.DoesNotContain(host.Services.GetRequiredService<IRuleTemplateRegistry>().All, t => t.Key == "IntegrationHealth");
        }
    }
}
