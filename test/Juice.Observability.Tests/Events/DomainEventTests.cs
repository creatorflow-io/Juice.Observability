using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juice.Messaging;
using Juice.Observability.Api;
using Juice.Observability.Api.Modules.Events;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Extensibility;
using Juice.Observability.Rules;
using Juice.Observability.Tests.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests.Events
{
    /// <summary>Events for modules: written in the change's transaction, one envelope per module that handles them, delivered after the commit.</summary>
    [Trait("Milestone", "C2-M2")]
    public sealed class DomainEventTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static void Both(WebApplicationBuilder b)
        {
            b.AddObservabilityModule<EventsOneModule>();
            b.AddObservabilityModule<EventsTwoModule>();
        }

        private static async Task<long> EnvelopesAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            return await db.Outbox.CountAsync(o => o.EventTypeName == nameof(ModuleEventEnvelope), Ct);
        }

        private static Task<HttpResponseMessage> ReportAsync(ModuleTestHost host, string? externalKey = null)
            => host.As("operator").PostAsJsonAsync($"/api/v1/tenants/{host.TenantId}/changes", new { kind = "deployment", summary = "deployed", externalKey }, Ct);

        private static async Task<T> WaitForAsync<T>(Func<T?> read, int seconds = 40) where T : class
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                if (read() is { } value)
                {
                    return value;
                }
                await Task.Delay(50, Ct);
            }
            throw new TimeoutException("Not seen in time");
        }

        [Fact]
        public void Only_the_core_events_have_handlers_and_a_handler_twice_is_refused()
        {
            var builder = CoreHost.CreateBuilder();
            builder.Services.AddJuiceObservability(builder.Configuration);
            Assert.Contains("not one of the core's events", Assert.Throws<ModuleCompositionException>(() => builder.AddObservabilityModule<AbstractEventModule>()).Message);
            Assert.Contains("twice", Assert.Throws<ModuleCompositionException>(() => builder.AddObservabilityModule<DoubleHandlerModule>()).Message);
        }

        [Fact]
        public async Task No_subscriber_writes_no_outbox_row()
        {
            LocalStack.RequireAvailable();
            await using var plain = await LocalStack.CreateDatabaseAsync();
            await using var heard = await LocalStack.CreateDatabaseAsync();
            await using (var host = await ModuleTestHost.StartAsync(null, plain))
            {
                Assert.Equal(HttpStatusCode.Created, (await ReportAsync(host)).StatusCode);
            }
            await using (var host = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<EventsOneModule>(), heard))
            {
                Assert.Equal(HttpStatusCode.Created, (await ReportAsync(host)).StatusCode);
            }

            Assert.Equal(0, await EnvelopesAsync(plain));
            Assert.Equal(1, await EnvelopesAsync(heard));
        }

        [Fact]
        public async Task Change_ingest_replay_same_external_key_no_second_event()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<EventsOneModule>(), database);

            Assert.Equal(HttpStatusCode.Created, (await ReportAsync(host, "pipeline-1")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await ReportAsync(host, "pipeline-1")).StatusCode);

            Assert.Equal(1, await EnvelopesAsync(database));
        }

        [Fact]
        public async Task One_envelope_per_subscribing_module_failure_retries_only_that_module()
        {
            LocalStack.RequireAvailable();
            EventProbe.Reset();
            EventProbe.TwoFailures = 1;
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await ModuleTestHost.StartAsync(Both, database, hostedServices: true);

            Assert.Equal(HttpStatusCode.Created, (await ReportAsync(host)).StatusCode);
            Assert.Equal(2, await EnvelopesAsync(database));

            // module two fails once and is handed the event again (the outbox's retry), module one has it once
            await WaitForAsync(() => EventProbe.Two.Count >= 2 ? "retried" : null);
            await WaitForAsync(() => EventProbe.One.Count >= 1 ? "one" : null);
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            Assert.Single(EventProbe.One);
            Assert.Equal(2, EventProbe.Two.Count);
            Assert.Equal(EventProbe.Two.First().EventId, EventProbe.Two.Last().EventId);
        }

        [Fact]
        public async Task Events_carry_platform_tenant_only_and_Restart_between_commit_and_dispatch_delivers_later()
        {
            LocalStack.RequireAvailable();
            EventProbe.Reset();
            await using var database = await LocalStack.CreateDatabaseAsync();
            Guid tenant;
            // committed, and the host stops before anything is delivered: no delivery runs without hosted services
            await using (var first = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<EventsOneModule>(), database))
            {
                tenant = first.TenantId;
                Assert.Equal(HttpStatusCode.Created, (await ReportAsync(first)).StatusCode);
            }
            Assert.Empty(EventProbe.One);

            await using var second = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<EventsOneModule>(), database, hostedServices: true);

            var heard = await WaitForAsync(() => EventProbe.One.OfType<ChangeRecordedEvent>().FirstOrDefault());
            Assert.Equal(tenant, heard.TenantId);
            Assert.Equal("Deployment", heard.Kind);
            await using var db = database.Context();
            var change = await db.ChangeEvents.SingleAsync(Ct);
            Assert.Equal(change.Id, heard.ChangeEventId);
            Assert.NotEqual(Guid.Empty, heard.EventId);
        }

        [Fact]
        public async Task Redelivery_same_EventId()
        {
            await using var host = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<EventsOneModule>());
            EventProbe.Reset();
            var sent = new ChangeRecordedEvent(Guid.NewGuid(), "Deployment", null) { EventId = Guid.NewGuid(), TenantId = Guid.NewGuid(), At = DateTimeOffset.UtcNow };
            var envelope = new ModuleEventEnvelope("evone", nameof(ChangeRecordedEvent), JsonSerializer.Serialize(sent, ModuleEventJson.Options));
            var consumer = ActivatorUtilities.CreateInstance<ModuleEventEnvelopeConsumer>(host.App.Services);

            // local dispatch, then the outbox's delivery
            await consumer.HandleAsync(envelope);
            await consumer.HandleAsync(envelope);

            Assert.Equal([sent, sent], EventProbe.One.ToArray());
        }

        [Fact]
        public async Task Event_committed_with_change_none_on_rollback()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<EventsOneModule>(), database);

            async Task ApplyAsync(bool commit)
            {
                await using var scope = host.App.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                var at = DateTimeOffset.UtcNow.AddMinutes(-5);
                await new Reconciler(db, new Normalizer([]), new DiscoveryOptions(), TimeProvider.System, scope.ServiceProvider.GetRequiredService<IPlatformEventPublisher>())
                    .ApplyAsync(host.TenantId, [new ResourceObservation { ServiceName = "orders", InstanceId = "i1", Environment = "production", FirstSeen = at, LastSeen = at, Records = 1 }], [], [], Ct);
                // inside the transaction the rows are there; the change and its events live or die together
                Assert.True(await db.Outbox.CountAsync(o => o.EventTypeName == nameof(ModuleEventEnvelope), Ct) >= 2);
                if (commit)
                {
                    await transaction.CommitAsync(Ct);
                }
                else
                {
                    await transaction.RollbackAsync(Ct);
                }
            }

            await ApplyAsync(commit: false);
            Assert.Equal(0, await EnvelopesAsync(database));
            await using (var check = database.Context())
            {
                Assert.Empty(await check.Services.ToListAsync(Ct));
            }

            await ApplyAsync(commit: true);
            await using var db = database.Context();
            var created = (await db.Outbox.Where(o => o.EventTypeName == nameof(ModuleEventEnvelope)).ToListAsync(Ct))
                .Select(o => System.Text.Encoding.UTF8.GetString(o.PayloadBytes)).ToList();
            Assert.Contains(created, p => p.Contains("service") && p.Contains("created"));
            Assert.Contains(created, p => p.Contains("instance") && p.Contains("created"));
        }

        [Fact]
        public void Alert_fire_and_resolve_after_fire_raise_event_pending_does_not()
        {
            using var db = new ScratchDatabase("none").Context();
            Alert Open(DateTimeOffset? firedAt) => new() { TenantId = Guid.NewGuid(), DedupKey = "r|s", SubjectKey = "s", State = AlertState.Pending, FiredAt = firedAt };

            var pending = Open(null);
            AlertChanges.Resolve(db, pending, "recovered", DateTimeOffset.UtcNow, RuleTemplate.ErrorRate);
            Assert.Empty(pending.DomainEvents);

            var firing = Open(DateTimeOffset.UtcNow);
            firing.State = AlertState.Firing;
            AlertChanges.Resolve(db, firing, "recovered", DateTimeOffset.UtcNow, RuleTemplate.ErrorRate);
            Assert.IsType<AlertTransitioned>(Assert.Single(firing.DomainEvents));
        }

        [Fact]
        public async Task Alert_transition_becomes_one_event_for_the_module_in_the_same_transaction()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<EventsOneModule>(), database);
            await using var scope = host.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var rule = new Rule { TenantId = host.TenantId, Key = "error-rate-orders", Name = "Orders errors", Template = RuleTemplate.ErrorRate, Severity = AlertSeverity.Critical, Definition = "{}", DefinitionHash = "h" };
            var alert = new Alert { TenantId = host.TenantId, RuleId = rule.Id, DedupKey = "error-rate-orders|service:orders", SubjectKey = "service:orders", Severity = AlertSeverity.Critical, State = AlertState.Firing, FiredAt = DateTimeOffset.UtcNow };
            var transition = new AlertTransition { TenantId = host.TenantId, AlertId = alert.Id, ToState = AlertState.Firing, At = DateTimeOffset.UtcNow, Reason = "breach" };
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            db.AddRange(rule, alert, transition);
            await db.SaveChangesAsync(Ct);
            using var context = PlatformMessageContext.Begin("test");

            await ActivatorUtilities.CreateInstance<PublishAlertTransitioned>(scope.ServiceProvider)
                .Handle(new AlertTransitioned(host.TenantId, alert.Id, transition.Id, AlertState.Firing), Ct);
            await transaction.CommitAsync(Ct);

            await using var check = database.Context();
            var payload = System.Text.Encoding.UTF8.GetString((await check.Outbox.SingleAsync(o => o.EventTypeName == nameof(ModuleEventEnvelope), Ct)).PayloadBytes);
            Assert.Contains("error-rate-orders", payload);
            Assert.Contains("ErrorRate", payload);
            Assert.Contains("Firing", payload);
            Assert.Contains("Critical", payload);
        }

        [Fact]
        public async Task Publishing_without_an_open_transaction_is_refused()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var host = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<EventsOneModule>(), database);
            await using var scope = host.App.Services.CreateAsyncScope();

            var e = new ChangeRecordedEvent(Guid.NewGuid(), "Deployment", null) { EventId = Guid.NewGuid(), TenantId = host.TenantId, At = DateTimeOffset.UtcNow };
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IPlatformEventPublisher>().PublishAsync(e, Ct));
            Assert.Equal(0, await EnvelopesAsync(database));
        }
    }

    public sealed class AbstractEventModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("abs", "Abs", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddEventHandler<CustomEvent, CustomHandler>();
    }

    public sealed record CustomEvent : ObservabilityEvent;

    public sealed class CustomHandler : IObservabilityEventHandler<CustomEvent>
    {
        public Task HandleAsync(CustomEvent e, CancellationToken ct) => Task.CompletedTask;
    }

    public sealed class DoubleHandlerModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("dbl", "Dbl", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module)
            => module.AddEventHandler<ChangeRecordedEvent, OneChangeHandler>().AddEventHandler<ChangeRecordedEvent, OneChangeHandler>();
    }
}
