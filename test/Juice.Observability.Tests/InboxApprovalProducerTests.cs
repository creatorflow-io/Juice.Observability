using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Notifications;
using Juice.Notifications.Rendering;
using Juice.Observability.Agents;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Juice.Observability.Tests.InboxHostTests;
using static Juice.Observability.Tests.InboxProducerTests;
using static Juice.Observability.Tests.ProducerAdapterTests;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// An action request as an inbox item (M56, slice C): who the approval names, what happens to it when the inbox is off, the pipeline is down or
    /// its roles are refused, how it is resolved, and its template. It is never sent to a route.
    /// </summary>
    [Trait("Milestone", "M56")]
    public sealed class InboxApprovalProducerTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static Dictionary<string, string> Off() => new(Quiet()) { ["Notifications:ConsoleUrl"] = "https://console.example.com" };

        private static async Task<ActionRequest> RequestAsync(ScratchDatabase database, string policy = ApprovalPolicies.SecondPerson, ActionRequestState state = ActionRequestState.Pending,
            string? decidedBy = null, ActionVerification verification = ActionVerification.None)
            => new()
            {
                TenantId = await TenantAsync(database), AgentId = Guid.NewGuid(), Action = "service.restart", Parameters = """{"name":"web"}""", Risk = "medium", Policy = policy,
                Reason = "stuck", RequestedBy = "erin", RequestedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), State = state,
                DecidedBy = decidedBy, DecidedAt = decidedBy is null ? null : DateTimeOffset.UtcNow, Verification = verification,
            };

        /// <summary>Tells the pipeline in a transaction of the platform's, as the endpoints do.</summary>
        private static async Task NotifyAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, ActionRequest request, string type, string hostname = "web-1")
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            await ActionRequestNotices.AddAsync(scope.ServiceProvider, request, hostname, type, DateTimeOffset.UtcNow, Ct);
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        }

        private static async Task<string?> OutcomeAsync(ScratchDatabase database, string key)
            => (string?)await ScalarAsync(database, $"SELECT outcome FROM notifications.correlations WHERE key = '{key}'");

        /// <summary>The refusals the platform counted while <paramref name="work"/> ran, as <c>call/code</c>.</summary>
        private static async Task<List<string>> CountedRefusalsAsync(Func<Task> work)
        {
            var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "juice.observability.notifications.refused")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                string? call = null, code = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "call") call = tag.Value?.ToString();
                    if (tag.Key == "code") code = tag.Value?.ToString();
                }
                seen.Enqueue($"{call}/{code}");
            });
            listener.Start();
            await work();
            return [.. seen];
        }

        [Fact]
        public async Task ApprovalItem_InboxOn_Created_AdminsOnly_OnePerRequest_KeyedAndCorrelated_NoRoute()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database, settings: On());
            _ = factory.Server;
            var request = await RequestAsync(database);

            await NotifyAsync(factory, request, ActionRequestEvents.Created);

            var sent = await RequestsAsync(database);
            Assert.Equal([ObservabilityTypes.ActionApproval, ObservabilityTypes.ForPlatformEvent(ActionRequestEvents.Created)], sent.Select(r => r.Type));
            var approval = sent[0];
            Assert.Equal(($"approval:{request.Id}", $"approval:{request.Id}"), (approval.Key, approval.Correlation));
            // The admins who signed in, and nothing else: no route, and not the operators and admins an alert names.
            Assert.Equal(["role:admin"], Named(Assert.Single((await RecipientsAsync(database)).Take(1))));
            Assert.Equal("https://console.example.com/approvals", approval.Links["self"]);
            // The notice routes receive is as it was: its own correlation, and the routes it matches (the default).
            Assert.Equal($"action:{request.Id}", sent[1].Correlation);
            Assert.Equal(["route"], Named((await RecipientsAsync(database))[1]));
            Assert.Equal("Open", await CorrelationStateAsync(database, $"approval:{request.Id}"));
        }

        [Fact]
        public async Task ApprovalItem_InboxOff_NotSent_RouteNoticeAsBefore_ResolveNoOp_NoError()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var capture = new LogCapture();
            await using var factory = new ApiFactory(database, settings: Off(), services: s => s.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(capture));
            _ = factory.Server;
            var request = await RequestAsync(database);

            var refused = await CountedRefusalsAsync(async () =>
            {
                await NotifyAsync(factory, request, ActionRequestEvents.Created);
                request.State = ActionRequestState.Rejected;
                request.DecidedBy = "sam";
                await NotifyAsync(factory, request, ActionRequestEvents.Rejected);
            });

            Assert.Equal([ObservabilityTypes.ForPlatformEvent(ActionRequestEvents.Created), ObservabilityTypes.ForPlatformEvent(ActionRequestEvents.Rejected)],
                (await RequestsAsync(database)).Select(r => r.Type));
            Assert.Null(await CorrelationStateAsync(database, $"approval:{request.Id}"));
            // Nothing refused, nothing logged as an error: the resolve of an item that was never made is a no-op.
            Assert.Empty(refused);
            Assert.DoesNotContain(capture.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("approval", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task ApprovalItem_RolesRefused_Dropped_NotResentToRoutes_Counted()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var capture = new LogCapture();
            await using var factory = new ApiFactory(database, settings: On(), services: s => s.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(capture));
            _ = factory.Server;
            // Another replica restarted with the inbox off and disabled the shared resolver.
            await ScalarAsync(database, "WITH u AS (UPDATE notifications.recipient_resolvers SET enabled = false WHERE key = 'signed-in' RETURNING 1) SELECT count(*) FROM u");
            var request = await RequestAsync(database);

            var refused = await CountedRefusalsAsync(() => NotifyAsync(factory, request, ActionRequestEvents.Created));

            // Only the route notice went: the approval was not sent again without its roles (its default recipients are the routes it matches).
            Assert.Equal([ObservabilityTypes.ForPlatformEvent(ActionRequestEvents.Created)], (await RequestsAsync(database)).Select(r => r.Type));
            Assert.Contains("send/inbox_off", refused);
            Assert.Contains(capture.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("is dropped", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ApprovalItem_DecidedWhileInboxOff_ItemResolvedWhenOnAgain_Outcome_Bounded()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var request = await RequestAsync(database);
            await using (var on = new ApiFactory(database, settings: On()))
            {
                _ = on.Server;
                await NotifyAsync(on, request, ActionRequestEvents.Created);
            }
            // The inbox is turned off, and the request is decided: the item made while it was on still ends.
            request.State = ActionRequestState.Rejected;
            request.DecidedBy = new string('x', 300);
            await using (var off = new ApiFactory(database, settings: Off()))
            {
                _ = off.Server;
                await NotifyAsync(off, request, ActionRequestEvents.Rejected);
            }

            Assert.Equal("Resolved", await CorrelationStateAsync(database, $"approval:{request.Id}"));
            var outcome = await OutcomeAsync(database, $"approval:{request.Id}");
            Assert.Equal(200, outcome!.Length);
            Assert.StartsWith("Rejected by xxx", outcome);
        }

        [Fact]
        public async Task ApprovalItem_ApprovedWithTarget_Verifying_ApprovalResolved_ActionCorrelationOpen()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database, settings: On());
            _ = factory.Server;
            var request = await RequestAsync(database);
            await NotifyAsync(factory, request, ActionRequestEvents.Created);

            request.State = ActionRequestState.Issued;
            request.DecidedBy = "sam";
            request.Verification = ActionVerification.Verifying;
            await NotifyAsync(factory, request, ActionRequestEvents.Approved);

            Assert.Equal("Resolved", await CorrelationStateAsync(database, $"approval:{request.Id}"));
            Assert.Equal("Approved by sam", await OutcomeAsync(database, $"approval:{request.Id}"));
            // The request is not done: it is verifying, so its own correlation stays open for the verification's notice.
            Assert.Equal("Open", await CorrelationStateAsync(database, $"action:{request.Id}"));
        }

        [Theory]
        [InlineData(ActionRequestEvents.Rejected, "sam", "Rejected by sam")]
        [InlineData(ActionRequestEvents.Cancelled, "erin", "Cancelled by erin")]
        [InlineData(ActionRequestEvents.Expired, null, "Expired by platform")]
        public async Task ApprovalItem_Ended_ResolvedWithItsOutcome_SweeperIsPlatform(string type, string? decidedBy, string expected)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiFactory(database, settings: On());
            _ = factory.Server;
            var request = await RequestAsync(database);
            await NotifyAsync(factory, request, ActionRequestEvents.Created);

            request.DecidedBy = decidedBy;
            await NotifyAsync(factory, request, type);

            Assert.Equal("Resolved", await CorrelationStateAsync(database, $"approval:{request.Id}"));
            Assert.Equal(expected, await OutcomeAsync(database, $"approval:{request.Id}"));
            Assert.Equal("Resolved", await CorrelationStateAsync(database, $"action:{request.Id}"));
        }

        // ─── The pipeline is down ─────────────────────────────────────────────

        private static async Task<string> BrokenProvisioningAsync()
        {
            var file = Path.Combine(Path.GetTempPath(), $"juice-obs-provisioning-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(file, $$"""
                { "tenants": { "{{LocalStack.TenantKey}}": { "integrations": { "mail": { "channel": "email", "kind": "smtp", "settings": { "server": "none" } } } } } }
                """, Ct);
            return file;
        }

        private static NotificationsReleasePass Pass(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
            => new(factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new PlatformNotificationsOptions()), TimeProvider.System,
                NullLogger<NotificationsReleasePass>.Instance);

        [Fact]
        public async Task ApprovalResolve_ParkedWhileDown_ReplayedInOrder_BeforeItsNotice()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var request = await RequestAsync(database);
            await using (var up = new ApiFactory(database, settings: On()))
            {
                _ = up.Server;
                await NotifyAsync(up, request, ActionRequestEvents.Created);
                Assert.Equal("Open", await CorrelationStateAsync(database, $"approval:{request.Id}"));
            }
            var file = await BrokenProvisioningAsync();
            try
            {
                var down = On(new() { ["Notifications:Provisioning:Path"] = file });
                await using var factory = new ApiFactory(database, settings: down);
                _ = factory.Server;
                request.State = ActionRequestState.Rejected;
                request.DecidedBy = "sam";
                await NotifyAsync(factory, request, ActionRequestEvents.Rejected);
                await using var check = database.Context();
                // The resolve of the item first, then the notice (with the resolve of the request's own correlation).
                Assert.Equal([NotificationParkedCalls.Resolve, NotificationParkedCalls.Send],
                    await check.NotificationParkedCalls.OrderBy(c => c.Id).Select(c => c.Kind).ToListAsync(Ct));
                Assert.Equal("Open", await CorrelationStateAsync(database, $"approval:{request.Id}"));
            }
            finally
            {
                File.Delete(file);
            }

            await using (var healthy = new ApiFactory(database, settings: On()))
            {
                _ = healthy.Server;
                await Pass(healthy).ReleaseOnceAsync(Ct);
                Assert.Equal("Resolved", await CorrelationStateAsync(database, $"approval:{request.Id}"));
                Assert.Equal("Rejected by sam", await OutcomeAsync(database, $"approval:{request.Id}"));
                await using var check = database.Context();
                Assert.All(await check.NotificationParkedCalls.ToListAsync(Ct), c => Assert.NotNull(c.ReplayedAt));
                Assert.Equal(0, await Pass(healthy).ReleaseOnceAsync(Ct));
            }
        }

        [Fact]
        public async Task ApprovalItem_ParkedWhileDown_ReplayedAfterInboxOff_Dropped_NotSentToRoutes()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var request = await RequestAsync(database);
            var file = await BrokenProvisioningAsync();
            try
            {
                await using var down = new ApiFactory(database, settings: On(new() { ["Notifications:Provisioning:Path"] = file }));
                _ = down.Server;
                await NotifyAsync(down, request, ActionRequestEvents.Created);
                await using var check = database.Context();
                // The notice and the approval, kept; the approval names its roles.
                var parked = await check.NotificationParkedCalls.OrderBy(c => c.Id).ToListAsync(Ct);
                Assert.Equal([NotificationParkedCalls.Send, NotificationParkedCalls.Send], parked.Select(c => c.Kind));
                Assert.Contains(parked, c => c.Request!.Contains(ObservabilityTypes.ActionApproval, StringComparison.Ordinal));
            }
            finally
            {
                File.Delete(file);
            }

            // Up again, with the inbox off: the approval is dropped, never sent with the routes it matches; the route notice goes as before.
            var refused = new List<string>();
            await using var off = new ApiFactory(database, settings: Off());
            _ = off.Server;
            refused.AddRange(await CountedRefusalsAsync(async () => await Pass(off).ReleaseOnceAsync(Ct)));

            Assert.Equal([ObservabilityTypes.ForPlatformEvent(ActionRequestEvents.Created)], (await RequestsAsync(database)).Select(r => r.Type));
            Assert.Contains("send/inbox_off", refused);
            await using var after = database.Context();
            Assert.All(await after.NotificationParkedCalls.ToListAsync(Ct), c => Assert.NotNull(c.ReplayedAt));
        }

        // ─── A request decided before the kept item is sent ───────────────────

        private static async Task SaveAsync(ScratchDatabase database, ActionRequest request)
        {
            await using var db = database.Context();
            db.Agents.Add(new EnrolledAgent
            {
                Id = request.AgentId, TenantId = request.TenantId, Hostname = "web-1", CertificateSerial = "01", CertificateNotAfter = DateTimeOffset.UtcNow.AddYears(1),
                EnrolledBy = "test", EnrolledAt = DateTimeOffset.UtcNow,
            });
            db.ActionRequests.Add(request);
            await db.SaveChangesAsync(Ct);
        }

        /// <summary>The item made while the pipeline was down, and the request's state when the release pass sends it.</summary>
        private static async Task<(ActionRequest Request, string Correlation)> KeptWhileDownAsync(ScratchDatabase database)
        {
            var request = await RequestAsync(database);
            await SaveAsync(database, request);
            var file = await BrokenProvisioningAsync();
            try
            {
                await using var down = new ApiFactory(database, settings: On(new() { ["Notifications:Provisioning:Path"] = file }));
                _ = down.Server;
                await NotifyAsync(down, request, ActionRequestEvents.Created);
            }
            finally
            {
                File.Delete(file);
            }
            return (request, $"approval:{request.Id}");
        }

        [Fact]
        public async Task ApprovalItem_KeptWhileDown_RequestDecidedBeforeTheReplay_NotSent_NoOpenItem()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (request, correlation) = await KeptWhileDownAsync(database);

            await using var up = new ApiFactory(database, settings: On());
            _ = up.Server;
            // Decided live after the restart and before the release pass: its resolve finds no item yet.
            await using (var db = database.Context())
            {
                await db.ActionRequests.Where(r => r.Id == request.Id).ExecuteUpdateAsync(r => r.SetProperty(x => x.State, ActionRequestState.Rejected).SetProperty(x => x.DecidedBy, "sam"), Ct);
            }
            request.State = ActionRequestState.Rejected;
            request.DecidedBy = "sam";
            await NotifyAsync(up, request, ActionRequestEvents.Rejected);
            await Pass(up).ReleaseOnceAsync(Ct);

            Assert.Null(await CorrelationStateAsync(database, correlation));
            Assert.DoesNotContain(await RequestsAsync(database), r => r.Type == ObservabilityTypes.ActionApproval);
            await using var check = database.Context();
            Assert.All(await check.NotificationParkedCalls.ToListAsync(Ct), c => Assert.NotNull(c.ReplayedAt));
        }

        [Fact]
        public async Task ApprovalItem_KeptWhileDown_DecisionHoldsTheRequestRow_PassDoesNotWait_KeepsItParked_NextPassDrops()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (request, correlation) = await KeptWhileDownAsync(database);
            await using var up = new ApiFactory(database, settings: On());
            _ = up.Server;

            // A decision is in progress: it holds the request's row, and has not committed.
            await using var decision = database.Context();
            await using var transaction = await decision.Database.BeginTransactionAsync(Ct);
            await decision.Database.ExecuteSqlAsync($"SELECT 1 FROM action_requests WHERE id = {request.Id} FOR UPDATE", Ct);
            var started = System.Diagnostics.Stopwatch.StartNew();
            await Pass(up).ReleaseOnceAsync(Ct);

            // The pass went on without waiting for it, and kept the item: not sent, not given up, no attempt counted.
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"the pass waited {started.Elapsed}");
            Assert.Null(await CorrelationStateAsync(database, correlation));
            await using (var check = database.Context())
            {
                var kept = (await check.NotificationParkedCalls.AsNoTracking().ToListAsync(Ct)).Single(c => c.Request!.Contains(ObservabilityTypes.ActionApproval, StringComparison.Ordinal));
                Assert.Equal((null, 0), (kept.ReplayedAt, kept.Attempts));
            }

            // The decision commits; the next pass finds the request decided and drops the item.
            await decision.ActionRequests.Where(r => r.Id == request.Id).ExecuteUpdateAsync(r => r.SetProperty(x => x.State, ActionRequestState.Rejected), Ct);
            await transaction.CommitAsync(Ct);
            await Pass(up).ReleaseOnceAsync(Ct);

            Assert.Null(await CorrelationStateAsync(database, correlation));
            await using var after = database.Context();
            Assert.All(await after.NotificationParkedCalls.ToListAsync(Ct), c => Assert.NotNull(c.ReplayedAt));
            Assert.DoesNotContain(await RequestsAsync(database), r => r.Type == ObservabilityTypes.ActionApproval);
        }

        [Fact]
        public async Task ApprovalItem_KeptWhileDown_RequestStillWaitingAtTheReplay_Sent_OpenUntilDecided()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var (request, correlation) = await KeptWhileDownAsync(database);

            await using var up = new ApiFactory(database, settings: On());
            _ = up.Server;
            await Pass(up).ReleaseOnceAsync(Ct);

            Assert.Equal("Open", await CorrelationStateAsync(database, correlation));
            Assert.Contains(await RequestsAsync(database), r => r.Type == ObservabilityTypes.ActionApproval && r.Correlation == correlation);
            // And a decision after it resolves it.
            request.State = ActionRequestState.Rejected;
            request.DecidedBy = "sam";
            await NotifyAsync(up, request, ActionRequestEvents.Rejected);
            Assert.Equal("Resolved", await CorrelationStateAsync(database, correlation));
        }

        // ─── A route that matches every observability type ────────────────────

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ApprovalItem_RouteMatchingEveryObservabilityType_NeverGetsTheApproval(bool inbox)
        {
            LocalStack.RequireAvailable();
            await using var receiver = await TestReceiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            Environment.SetEnvironmentVariable(ProvisionedHost.SecretVariable, ProvisionedHost.Secret);
            var path = Path.Combine(Path.GetTempPath(), $"juice-obs-m56-provisioning-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                tenants = new Dictionary<string, object>
                {
                    [LocalStack.TenantKey] = new
                    {
                        integrations = new Dictionary<string, object>
                        {
                            ["hooks"] = new { channel = "webhook", kind = "webhook", secretRef = $"env:{ProvisionedHost.SecretVariable}", targets = new Dictionary<string, object> { ["ops"] = new { address = receiver.Url } } },
                        },
                        routes = new Dictionary<string, object> { ["everything"] = new { match = new { type = "observability.*" }, targets = new[] { "ops" } } },
                        allowlist = new object[] { new { destination = $"{receiver.Address}/32", ports = new[] { receiver.Port } } },
                    },
                },
            }), Ct);
            try
            {
                var settings = ProvisionedHost.Settings(path);
                settings["Notifications:ConsoleUrl"] = "https://console.example.com";
                settings["Notifications:Inbox:Enabled"] = inbox ? "true" : "false";
                await using var factory = new ApiFactory(database, settings: settings);
                _ = factory.Server;
                var request = await RequestAsync(database);

                await NotifyAsync(factory, request, ActionRequestEvents.Created);

                // The request's own notice reaches the route (it is the one that is for routes); the approval is the pipeline's inbox item only.
                await ProvisionedHost.WaitAsync(() => !receiver.Requests.IsEmpty, "the notice to reach the route");
                await Task.Delay(TimeSpan.FromSeconds(3), Ct);
                Assert.Single(receiver.Requests);
                var approvals = (await RequestsAsync(database)).Where(r => r.Type == ObservabilityTypes.ActionApproval).ToList();
                Assert.Equal(inbox ? 1 : 0, approvals.Count);
                if (inbox)
                {
                    Assert.Equal(["role:admin"], Named((await RecipientsAsync(database))[0]));
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ─── The type and its template ────────────────────────────────────────

        [Fact]
        public void ApprovalType_IsDeclared_WithItsTwoActions_CorrelationRequired_ApprovalGroup()
        {
            var type = ObservabilityTypes.All().Single(t => t.Key == ObservabilityTypes.ActionApproval);

            Assert.Equal(("Action approval", "approval", CorrelationRules.Required), (type.Name, type.DisplayGroup, type.Correlation));
            Assert.Equal("""[{"id":"approve","label":"Approve"},{"id":"reject","label":"Reject"}]""", JsonSerializer.Serialize(type.Actions));
            // Only the inbox's template: an approval is never sent to a route.
            Assert.Equal(["inbox"], type.Templates!.Value.EnumerateObject().Select(p => p.Name));
            // The template's policy test names the value the platform writes.
            Assert.Contains($"data.request.policy == \"{ApprovalPolicies.SecondPerson}\"", DefaultTemplates.ApprovalInbox, StringComparison.Ordinal);
        }

        private static async Task<InboxContent> ItemAsync(string hostname = "web-1", string reason = "stuck", string policy = ApprovalPolicies.SecondPerson, string action = "service.restart",
            string requestedBy = "erin", IReadOnlyDictionary<string, string>? links = null)
        {
            await TemplateWarmUp.EnsureAsync();
            var request = new ActionRequest
            {
                TenantId = Guid.Parse(LocalStack.TenantKey), AgentId = Guid.NewGuid(), Action = action, Parameters = "{}", Risk = "medium", Policy = policy, Reason = reason,
                RequestedBy = requestedBy, RequestedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            };
            var data = ProducerAdapter.WithoutId(ActionRequestNotices.Payload(Guid.Empty, ActionRequestEvents.Created, LocalStack.TenantKey, request, hostname, DateTimeOffset.UtcNow));
            var linkSet = links ?? new Dictionary<string, string>();
            var rendered = await TemplateRenderer.RenderAsync(DefaultTemplates.ApprovalInbox, new RenderModel(ObservabilityTypes.ActionApproval, 1, "Action approval",
                NotificationPriority.Normal, data, new Dictionary<string, string>(), linkSet), Ct);
            return JsonSerializer.Deserialize<InboxContent>(ChannelContent.Inbox(rendered, ObservabilityTypes.ActionApproval, "Action approval", linkSet, null))!;
        }

        [Fact]
        public async Task ApprovalTemplate_TitleAndBody_SecondPersonSaysWhoCannot()
        {
            var item = await ItemAsync();

            Assert.Equal("Approve service.restart on web-1?", item.Title);
            Assert.Equal("medium risk · asked by erin: stuck\nneeds an admin other than erin", item.Body);
            Assert.Equal("medium risk · asked by erin: stuck", (await ItemAsync(policy: ApprovalPolicies.Admin)).Body);
        }

        [Fact]
        public async Task ApprovalTemplate_Markup_ControlChars_ReasonAt500_HostnameAt255And256_Escaped()
        {
            // Markup stays text; control characters, line breaks included, are spaces.
            var markup = await ItemAsync(hostname: "<b>web</b>\u0007\r\nx", reason: "<script>alert(1)</script>&amp;\u0007y\r\nBcc: z");
            Assert.Equal("Approve service.restart on <b>web</b>   x?", markup.Title);
            Assert.DoesNotContain(markup.Title, c => char.IsControl(c));
            Assert.StartsWith("medium risk · asked by erin: <script>alert(1)</script>&amp; y  Bcc: z", markup.Body);
            Assert.DoesNotContain(markup.Body, c => char.IsControl(c) && c != '\n');

            // A reason of 500 characters is whole in the body; the title holds 250 whatever the hostname.
            Assert.Contains(new string('r', 500), (await ItemAsync(reason: new string('r', 500))).Body, StringComparison.Ordinal);
            foreach (var length in new[] { 255, 256 })
            {
                var item = await ItemAsync(hostname: new string('h', length));
                Assert.Equal(250, item.Title.Length);
                Assert.EndsWith("…", item.Title);
                Assert.StartsWith("Approve service.restart on hhh", item.Title);
            }
            var whole = await ItemAsync(hostname: new string('h', 250 - "Approve service.restart on ?".Length));
            Assert.Equal(250, whole.Title.Length);
            Assert.EndsWith("?", whole.Title);
        }

        [Theory]
        [InlineData("HTTPS://Console.Example.COM:443//")]
        [InlineData("http://console.example.com:80/")]
        [InlineData("https://console.example.com/ops/")]
        [InlineData("https://console.example.com")]
        public async Task ApprovalUrl_MixedCaseHost_DefaultPort_TrailingSlashes_ByteEqualsLink(string console)
        {
            var link = new PlatformNotificationsOptions { ConsoleUrl = console }.Link("approvals")!;

            var item = await ItemAsync(links: new Dictionary<string, string> { ["self"] = link });

            Assert.Equal(link, item.Url);
            Assert.Equal($"{console.TrimEnd('/')}/approvals", item.Url);
        }
    }
}
