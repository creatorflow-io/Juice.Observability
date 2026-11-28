using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Notifications;
using Juice.Notifications.Rendering;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Juice.Observability.Rules.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Juice.Observability.Tests.InboxHostTests;
using static Juice.Observability.Tests.ProducerAdapterTests;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The platform as a producer of inbox items (M54, slice B): who an alert names, what happens to a request that named them when the inbox is
    /// turned off, and the item's template.
    /// </summary>
    [Trait("Milestone", "M54")]
    public sealed class InboxProducerTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>What the pipeline kept of a request's recipients: <c>route</c>, <c>role:operator</c>.</summary>
        internal static string[] Named(JsonElement[] recipients)
            => [.. recipients.Select(r => r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? $"{r.GetProperty("kind").GetString()}:{id.GetString()}" : r.GetProperty("kind").GetString()!)];

        [Fact]
        public async Task Firing_AddsConfiguredRoles_Resolved_AddsNone()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database, settings: On());
            _ = factory.Server;
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);
            await PlanAsync(factory, database, await ResolveAsync(database, alert));

            var recipients = await RecipientsAsync(database);
            Assert.Equal(2, recipients.Count);
            // The default (key absent): operators and admins, and the routes it matched (naming recipients replaces the default, so they are named).
            Assert.Equal(["route", "role:operator", "role:admin"], Named(recipients[0]));
            // The resolve names nobody: the items of the firing are updated by the correlation, not sent again.
            Assert.Equal(["route"], Named(recipients[1]));
        }

        /// <summary>
        /// A rolling toggle: another replica restarted with the inbox off and disabled the shared <c>signed-in</c> row, while this one still has it
        /// on. The pipeline refuses the role recipients, and the firing must still reach its routes (and its correlation exist for the acknowledge).
        /// </summary>
        [Fact]
        public async Task Firing_InboxOnHere_ResolverRowDisabledByAnotherReplica_IsSentWithoutRoles_WithAWarning()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var capture = new LogCapture();
            await using var factory = new ApiFactory(database, settings: On(), services: s => s.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(capture));
            _ = factory.Server;
            await using (var connection = new Npgsql.NpgsqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(Ct);
                await using var disable = new Npgsql.NpgsqlCommand("UPDATE notifications.recipient_resolvers SET enabled = false WHERE key = 'signed-in'", connection);
                Assert.Equal(1, await disable.ExecuteNonQueryAsync(Ct));
            }
            var (alert, firing) = await FireAsync(database);

            await PlanAsync(factory, database, firing);

            var request = Assert.Single(await RequestsAsync(database));
            Assert.Equal($"alert:{alert}", request.Correlation);
            Assert.Equal(["route"], Named(Assert.Single(await RecipientsAsync(database))));
            Assert.Contains(capture.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("without its role recipients", StringComparison.Ordinal));
            // The correlation is open, so the acknowledge and the resolve find it.
            Assert.Equal("open", (await CorrelationStateAsync(database, $"alert:{alert}"))?.ToLowerInvariant());
        }

        [Fact]
        public async Task Firing_NamesOnlyTheConfiguredRoles()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database, settings: On(new() { ["Notifications:Inbox:Roles:0"] = "viewer" }));
            _ = factory.Server;
            var (_, firing) = await FireAsync(database);
            await PlanAsync(factory, database, firing);

            Assert.Equal(["route", "role:viewer"], Named(Assert.Single(await RecipientsAsync(database))));
        }

        private static NotificationsReleasePass Pass(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
            => new(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new PlatformNotificationsOptions()), TimeProvider.System,
                NullLogger<NotificationsReleasePass>.Instance);

        private static Dictionary<string, string> Off() => new(Quiet()) { ["Notifications:ConsoleUrl"] = "https://console.example.com" };

        /// <summary>A firing held under a silence while the inbox was on: its request names the roles.</summary>
        private static async Task<(Guid Alert, Guid Silence, string Request)> HeldWhileOnAsync(ScratchDatabase database)
        {
            await using var on = new ApiFactory(database, settings: On());
            _ = on.Server;
            var silence = await SilenceAsync(database);
            var (alert, firing) = await FireAsync(database);
            await PlanAsync(on, database, firing);
            await using var db = database.Context();
            var hold = await db.NotificationHolds.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(["route", "role:operator", "role:admin"], Named(JsonDocument.Parse(hold.Request).RootElement.GetProperty("recipients").EnumerateArray().ToArray()));
            return (alert, silence, hold.Request);
        }

        [Fact]
        public async Task HeldWhileOn_ReleasedAfterOff_RolesStripped_RouteDelivered()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (alert, silence, _) = await HeldWhileOnAsync(database);
            await using (var db = database.Context())
            {
                await db.Silences.Where(s => s.Id == silence).ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelledAt, DateTimeOffset.UtcNow).SetProperty(x => x.CancelledBy, "erin"), Ct);
            }

            // The inbox is turned off, and the silence ends: the pipeline would refuse a role recipient, and the notice would be lost to the routes too.
            await using var off = new ApiFactory(database, settings: Off());
            _ = off.Server;
            Assert.Equal(1, await Pass(off).ReleaseOnceAsync(Ct));

            var request = Assert.Single(await RequestsAsync(database));
            Assert.Equal($"alert:{alert}", request.Correlation);
            Assert.Equal(["route"], Named(Assert.Single(await RecipientsAsync(database))));
            await using var check = database.Context();
            Assert.Equal(NotificationHoldOutcomes.Sent, (await check.NotificationHolds.AsNoTracking().SingleAsync(Ct)).Outcome);
            // The correlation is open for the routes' escalation.
            Assert.Equal("open", (await CorrelationStateAsync(database, $"alert:{alert}"))?.ToLowerInvariant());
        }

        [Fact]
        public async Task HeldWhileOn_ReleasedWhileStillOn_KeepsRoles()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (_, silence, _) = await HeldWhileOnAsync(database);
            await using (var db = database.Context())
            {
                await db.Silences.Where(s => s.Id == silence).ExecuteUpdateAsync(s => s.SetProperty(x => x.CancelledAt, DateTimeOffset.UtcNow).SetProperty(x => x.CancelledBy, "erin"), Ct);
            }

            await using var on = new ApiFactory(database, settings: On());
            _ = on.Server;
            Assert.Equal(1, await Pass(on).ReleaseOnceAsync(Ct));

            Assert.Equal(["route", "role:operator", "role:admin"], Named(Assert.Single(await RecipientsAsync(database))));
        }

        [Fact]
        public async Task ParkedSend_ReplayedAfterOff_RolesStripped_RouteDelivered()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (_, _, request) = await HeldWhileOnAsync(database);
            await using (var db = database.Context())
            {
                // The hold is decided elsewhere; the same request is a parked send instead.
                await db.NotificationHolds.ExecuteUpdateAsync(h => h.SetProperty(x => x.Outcome, NotificationHoldOutcomes.Dropped).SetProperty(x => x.DecidedAt, DateTimeOffset.UtcNow), Ct);
                db.NotificationParkedCalls.Add(new NotificationParkedCall
                {
                    TenantId = await TenantAsync(database), Kind = NotificationParkedCalls.Send, Request = request, CreatedAt = DateTimeOffset.UtcNow, Pipeline = "new",
                });
                await db.SaveChangesAsync(Ct);
            }

            await using var off = new ApiFactory(database, settings: Off());
            _ = off.Server;
            Assert.True(await Pass(off).ReleaseOnceAsync(Ct) >= 1);

            Assert.Single(await RequestsAsync(database));
            Assert.Equal(["route"], Named(Assert.Single(await RecipientsAsync(database))));
            await using var check = database.Context();
            Assert.NotNull((await check.NotificationParkedCalls.AsNoTracking().SingleAsync(Ct)).ReplayedAt);
        }

        // ─── The item's template ──────────────────────────────────────────────

        private static (Rule Rule, Alert Alert, AlertTransition Transition) Alert(string subject, string ruleName = "Orders errors", string reason = "error rate 25% over 5%", double? value = 0.25)
        {
            var tenant = Guid.Parse(LocalStack.TenantKey);
            var at = new DateTimeOffset(2026, 11, 26, 9, 30, 15, TimeSpan.Zero);
            var rule = new Rule { TenantId = tenant, Key = "orders-errors", Name = ruleName, Definition = "{}", DefinitionHash = "h" };
            var alert = new Alert
            {
                TenantId = tenant, RuleId = rule.Id, DedupKey = "d", SubjectKey = subject, Severity = AlertSeverity.Critical, State = AlertState.Firing,
                BreachStartedAt = at.AddMinutes(-3), FiredAt = at,
            };
            return (rule, alert, new AlertTransition { TenantId = tenant, AlertId = alert.Id, ToState = AlertState.Firing, At = at, Value = value, Reason = reason });
        }

        private static async Task<InboxContent> ItemAsync(string subject, string ruleName = "Orders errors", string reason = "error rate 25% over 5%", double? value = 0.25,
            IReadOnlyDictionary<string, string>? links = null)
        {
            await TemplateWarmUp.EnsureAsync();
            var (rule, alert, transition) = Alert(subject, ruleName, reason, value);
            var node = JsonNode.Parse(NotificationPlan.Payload(Guid.Empty, NotificationPlan.Firing, LocalStack.TenantKey, rule, alert, transition, null))!.AsObject();
            node.Remove("id");
            var links2 = links ?? new Dictionary<string, string>();
            var rendered = await TemplateRenderer.RenderAsync(DefaultTemplates.Inbox, new RenderModel(ObservabilityTypes.AlertFiring, 1, "Alert firing", NotificationPriority.High,
                JsonSerializer.SerializeToElement(node), new Dictionary<string, string>(), links2), Ct);
            return JsonSerializer.Deserialize<InboxContent>(ChannelContent.Inbox(rendered, ObservabilityTypes.AlertFiring, "Alert firing", links2, null))!;
        }

        [Fact]
        public async Task InboxTemplate_TitleIsSeverityRuleAndSubject_BodyIsReasonAndValue()
        {
            var item = await ItemAsync("service:shop/orders@production");

            Assert.Equal("[Critical] Orders errors: service:shop/orders@production", item.Title);
            Assert.Equal("Reason: error rate 25% over 5%\nValue: 0.25", item.Body);
            // No reason, no value: the body says so.
            var bare = await ItemAsync("service:shop/orders@production", reason: "", value: null);
            Assert.Equal("Value: no data", bare.Body);
        }

        [Fact]
        public async Task InboxTemplate_Markup_ControlChars_SubjectAtBound_And_Over_TitleBounded_Escaped()
        {
            // Markup stays text (the item is plain text, which the Console shows as such); control characters, line breaks included, are spaces.
            var markup = await ItemAsync("service:<script>alert(1)</script>&amp;\u0007x\r\nBcc: y", ruleName: "Rule\u0007<b>");
            Assert.Equal("[Critical] Rule <b>: service:<script>alert(1)</script>&amp; x  Bcc: y", markup.Title);
            Assert.DoesNotContain(markup.Title, c => char.IsControl(c));
            Assert.DoesNotContain(markup.Body, c => char.IsControl(c) && c != '\n');

            // The title holds 250: a subject that brings it to exactly 250 is whole, one more is cut with an ellipsis at 250.
            var prefix = "[Critical] Orders errors: ";
            foreach (var length in new[] { 250 - prefix.Length - 1, 250 - prefix.Length, 250 - prefix.Length + 1, 255, 256, 2000 })
            {
                var item = await ItemAsync(new string('s', length));
                Assert.True(item.Title.Length <= 250, $"{length}: {item.Title.Length}");
                if (prefix.Length + length <= 250)
                {
                    Assert.Equal(prefix + new string('s', length), item.Title);
                }
                else
                {
                    Assert.Equal(250, item.Title.Length);
                    Assert.EndsWith("…", item.Title);
                    Assert.StartsWith(prefix + "sss", item.Title);
                }
            }
        }

        [Theory]
        [InlineData("HTTPS://Console.Example.COM:443//")]
        [InlineData("http://console.example.com:80/")]
        [InlineData("https://console.example.com/ops/")]
        [InlineData("https://console.example.com")]
        public async Task InboxItemUrl_MixedCaseHost_DefaultPort_TrailingSlashes_ByteEqualsLink(string console)
        {
            var link = new PlatformNotificationsOptions { ConsoleUrl = console }.Link("alerts/0193f0aa-0000-7000-8000-0000000000a1")!;
            Assert.True(new PlatformNotificationsOptionsValidation().Validate(null, new PlatformNotificationsOptions { ConsoleUrl = console, Inbox = { Enabled = true } }).Succeeded);

            var item = await ItemAsync("service:shop/orders@production", links: new Dictionary<string, string> { ["self"] = link });

            // The same bytes as the link every other channel carries: never normalised.
            Assert.Equal(link, item.Url);
            Assert.Equal($"{console.TrimEnd('/')}/alerts/0193f0aa-0000-7000-8000-0000000000a1", item.Url);
        }
    }
}
