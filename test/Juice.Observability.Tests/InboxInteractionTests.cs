using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Juice.Observability.Agent.Contracts;
using Juice.Observability.Agents;
using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Juice.Observability.Tests.InboxHostTests;
using static Juice.Observability.Tests.ProducerAdapterTests;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The bell decides (M56, slice C): Approve, Reject and Acknowledge on an inbox item run the Approvals and Alert pages' own methods in-process,
    /// with the current token's authority, and every refusal is the page's refusal. Needs the local stack.
    /// </summary>
    [Trait("Milestone", "M56")]
    public sealed class InboxInteractionTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>A host with the inbox on, the pipeline's stages running, agents on (commands are signed) and one active agent.</summary>
        private sealed class Rig : IAsyncDisposable
        {
            public required AgentPki Pki { get; init; }
            public required ScratchDatabase Database { get; init; }
            public required ApiFactory Factory { get; init; }
            public required HttpClient Client { get; init; }
            public required Guid Tenant { get; init; }
            public required Guid Agent { get; init; }
            public required LogCapture Logs { get; init; }

            public static async Task<Rig> StartAsync(Dictionary<string, string>? extra = null, bool claimsHeader = false)
            {
                LocalStack.RequireAvailable();
                var database = await LocalStack.CreateDatabaseAsync();
                var pki = new AgentPki();
                var settings = new Dictionary<string, string>(pki.Settings(AgentPki.FreePort(), AgentPki.FreePort()))
                {
                    ["Notifications:Inbox:Enabled"] = "true", ["Notifications:ConsoleUrl"] = "https://console.example.com",
                    ["Notifications:ReleasePassInterval"] = "00:00:01", ["Notifications:HoldPassInterval"] = "01:00:00",
                };
                foreach (var (key, value) in extra ?? [])
                {
                    settings[key] = value;
                }
                var logs = new LogCapture();
                var factory = new ApiFactory(database, settings: settings, services: services =>
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                    if (claimsHeader)
                    {
                        SignInByHeader(services);
                    }
                });
                var client = factory.CreateClient();
                var tenant = await TenantAsync(database);
                Guid agent;
                await using (var db = database.Context())
                {
                    var row = new EnrolledAgent
                    {
                        TenantId = tenant, Hostname = "web-1", CertificateSerial = "01", CertificateNotAfter = DateTimeOffset.UtcNow.AddYears(1), EnrolledBy = "test",
                        EnrolledAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
                    };
                    db.Agents.Add(row);
                    await db.SaveChangesAsync(Ct);
                    agent = row.Id;
                }
                return new Rig { Database = database, Factory = factory, Client = client, Tenant = tenant, Agent = agent, Pki = pki, Logs = logs };
            }

            public async ValueTask DisposeAsync()
            {
                Client.Dispose();
                await Factory.DisposeAsync();
                await Database.DisposeAsync();
                Pki.Dispose();
            }

            public static async Task<Caller> CallerAsync(Rig rig, string user) => new(rig.Client, await LocalStack.TokenAsync(user));

            /// <summary>The request of <paramref name="service"/>'s restart (High risk: a second person must approve it), as <paramref name="by"/>.</summary>
            public async Task<Guid> RequestAsync(Caller by, string service = "nginx", Guid? alert = null)
            {
                var response = await by.PostAsync($"/api/v1/tenants/{Tenant}/agents/{Agent}/action-requests",
                    new { action = AgentActions.ServiceRestart, parameters = new { service }, reason = "stuck", alertId = alert });
                var body = await response.Content.ReadAsStringAsync(Ct);
                Assert.True(response.StatusCode == HttpStatusCode.Created, body);
                var json = JsonDocument.Parse(body).RootElement;
                Assert.Equal("Pending", json.GetProperty("state").GetString());
                return json.GetProperty("id").GetGuid();
            }

            public async Task<JsonElement[]> ItemsAsync(Caller caller, string? type = null)
                => [.. (await Caller.JsonAsync(await caller.GetAsync(Me()))).EnumerateArray().Where(i => type is null || i.GetProperty("type").GetString() == type)];

            /// <summary>The first item of the type (in <paramref name="state"/>, when given) that isn't one of <paramref name="except"/>, once there is one.</summary>
            public async Task<JsonElement> WaitItemAsync(Caller caller, string type, string? state = null, ISet<Guid>? except = null, string? titleContains = null)
            {
                JsonElement found = default;
                await ProvisionedHost.WaitAsync(() =>
                {
                    var match = ItemsAsync(caller, type).GetAwaiter().GetResult().Where(i => (except is null || !except.Contains(i.GetProperty("id").GetGuid()))
                        && (titleContains is null || i.GetProperty("title").GetString()!.Contains(titleContains, StringComparison.Ordinal))
                        && (state is null || string.Equals(i.GetProperty("correlation").GetProperty("state").GetString(), state, StringComparison.OrdinalIgnoreCase))).ToList();
                    if (match.Count == 0)
                    {
                        return false;
                    }
                    found = match[0];
                    return true;
                }, $"the {type} item {state}");
                return found;
            }

            public Task<HttpResponseMessage> BellAsync(Caller caller, Guid item, string action)
                => caller.PostAsync(Me($"/{item}/actions/{action}"), new { });

            public Task<HttpResponseMessage> BellAcknowledgeAsync(Caller caller, Guid item) => caller.PostAsync(Me($"/{item}/acknowledge"), new { });

            public async Task<ActionRequest> RequestRowAsync(Guid id)
            {
                await using var db = Database.Context();
                return await db.ActionRequests.AsNoTracking().SingleAsync(r => r.Id == id, Ct);
            }

            public async Task<int> CommandsAsync()
            {
                await using var db = Database.Context();
                return await db.AgentCommands.CountAsync(Ct);
            }

            public async Task<List<AuditRecord>> AuditAsync(string operation)
            {
                await using var db = Database.Context();
                return await db.AuditRecords.AsNoTracking().Where(a => a.Operation == operation).OrderBy(a => a.OccurredAt).ToListAsync(Ct);
            }

            public async Task<long> InteractionsAsync()
                => (long)(await ScalarAsync(Database, "SELECT count(*) FROM notifications.interactions WHERE kind IN ('action', 'acknowledged')") ?? 0L);
        }

        // ─── The decision ─────────────────────────────────────────────────────

        [Fact]
        public async Task Inbox_Approval_TwoAdmins_ApproveFromBell_CommandSigned_ItemResolvedForBoth_RequesterGetsNone()
        {
            await using var rig = await Rig.StartAsync();
            var (alice, adam, erin) = (await Rig.CallerAsync(rig, "alice"), await Rig.CallerAsync(rig, "adam"), await Rig.CallerAsync(rig, "erin"));
            // The people sign in (a call to their inbox), as they do when the Console opens.
            foreach (var caller in new[] { alice, adam, erin })
            {
                Assert.Empty(await rig.ItemsAsync(caller));
            }
            var heard = new ConcurrentQueue<string>();
            await using var hub = Hub(rig.Factory, await LocalStack.TokenAsync("adam"), heard);
            await hub.StartAsync(Ct);

            var request = await rig.RequestAsync(erin);

            var (aliceItem, adamItem) = (await rig.WaitItemAsync(alice, ObservabilityTypes.ActionApproval), await rig.WaitItemAsync(adam, ObservabilityTypes.ActionApproval));
            Assert.Equal("Approve service.restart on web-1?", aliceItem.GetProperty("title").GetString());
            Assert.Equal("https://console.example.com/approvals", aliceItem.GetProperty("url").GetString());
            Assert.Equal(["approve", "reject"], aliceItem.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("id").GetString()));
            Assert.Equal("approval", aliceItem.GetProperty("group").GetString());
            // The requester is no admin: no item; and nobody's route got the approval.
            Assert.Empty(await rig.ItemsAsync(erin, ObservabilityTypes.ActionApproval));
            Assert.DoesNotContain(await RequestsAsync(rig.Database), r => r.Type == ObservabilityTypes.ActionApproval && (r.Key != $"approval:{request}"));

            var approved = await rig.BellAsync(alice, aliceItem.GetProperty("id").GetGuid(), "approve");

            Assert.True(approved.StatusCode == HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
            var row = await rig.RequestRowAsync(request);
            Assert.Equal((ActionRequestState.Issued, "alice"), (row.State, row.DecidedBy));
            Assert.Equal(1, await rig.CommandsAsync());
            var resolved = await rig.WaitItemAsync(adam, ObservabilityTypes.ActionApproval, state: "resolved");
            Assert.Equal("Approved by alice", resolved.GetProperty("correlation").GetProperty("outcome").GetString());
            await ProvisionedHost.WaitAsync(() => !heard.IsEmpty, "the hub to tell the other admin");
            // The other admin's later approve finds it closed, and nothing more is signed.
            var late = await rig.BellAsync(adam, adamItem.GetProperty("id").GetGuid(), "approve");
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            Assert.Contains("correlation_closed", await late.Content.ReadAsStringAsync(Ct));
            Assert.Equal(1, await rig.CommandsAsync());
            Assert.Empty(await rig.ItemsAsync(erin, ObservabilityTypes.ActionApproval));
        }

        private static HubConnection Hub(ApiFactory factory, string token, ConcurrentQueue<string> heard)
        {
            var server = factory.Server;
            var hub = new HubConnectionBuilder().WithUrl(new Uri(server.BaseAddress, $"notifications/hubs/inbox?tenant={Tenant}"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.WebSocketFactory = async (context, ct) =>
                    await server.CreateWebSocketClient().ConnectAsync(new Uri($"{context.Uri}&access_token={Uri.EscapeDataString(token)}"), ct);
            }).Build();
            hub.On<JsonElement>("inboxChanged", message => heard.Enqueue(message.GetProperty("kind").GetString()!));
            return hub;
        }

        /// <summary>Everything an audit record says of a decision but the ids and the time, so the page's and the bell's can be compared row by row.</summary>
        private static string Shape(AuditRecord a, Guid request, Guid command)
            => string.Join("|", a.Actor, a.Operation, a.TargetType, a.Outcome, string.Join(",", a.Metadata.Where(m => m.Key != "via")
                .OrderBy(m => m.Key).Select(m => $"{m.Key}={m.Value.Replace(request.ToString(), "<request>").Replace(command.ToString(), "<command>")}")));

        [Fact]
        public async Task BellApprove_SameAuditOperation_ViaInbox_CommandReferenceAsPage()
        {
            await using var rig = await Rig.StartAsync();
            var (alice, adam, erin) = (await Rig.CallerAsync(rig, "alice"), await Rig.CallerAsync(rig, "adam"), await Rig.CallerAsync(rig, "erin"));
            Assert.Empty(await rig.ItemsAsync(alice));
            var byPage = await rig.RequestAsync(erin, "nginx");
            await rig.WaitItemAsync(alice, ObservabilityTypes.ActionApproval);
            var byBell = await rig.RequestAsync(erin, "sshd");
            // The second item is the one that isn't the first (two requests, one agent: the items differ by when they came).
            var first = (await rig.ItemsAsync(alice, ObservabilityTypes.ActionApproval)).Select(i => i.GetProperty("id").GetGuid()).ToHashSet();
            var item = await rig.WaitItemAsync(alice, ObservabilityTypes.ActionApproval, except: first);

            Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync($"/api/v1/tenants/{rig.Tenant}/action-requests/{byPage}/approval", new { comment = (string?)null })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await rig.BellAsync(alice, item.GetProperty("id").GetGuid(), "approve")).StatusCode);

            var (page, bell) = (await rig.RequestRowAsync(byPage), await rig.RequestRowAsync(byBell));
            Assert.Equal((page.State, page.DecidedBy, page.Comment, page.Verification), (bell.State, bell.DecidedBy, bell.Comment, bell.Verification));
            await using var db = rig.Database.Context();
            var (pageCommand, bellCommand) = (await db.AgentCommands.SingleAsync(c => c.Id == page.CommandId, Ct), await db.AgentCommands.SingleAsync(c => c.Id == bell.CommandId, Ct));
            Assert.Equal($"action-request:{byPage};approved-by:alice", ActionRequestEndpointsReference(page));
            Assert.Equal($"action-request:{byBell};approved-by:alice", ActionRequestEndpointsReference(bell));
            Assert.Equal((pageCommand.Action, pageCommand.IssuedBy), (bellCommand.Action, bellCommand.IssuedBy));
            var audits = await rig.AuditAsync("action.request.approve");
            Assert.Equal(2, audits.Count);
            Assert.Equal(Shape(audits[0], byPage, pageCommand.Id), Shape(audits[1], byBell, bellCommand.Id));
            Assert.False(audits[0].Metadata.ContainsKey("via"));
            Assert.Equal("inbox", audits[1].Metadata["via"]);
        }

        private static string ActionRequestEndpointsReference(ActionRequest request) => $"action-request:{request.Id};approved-by:{request.DecidedBy ?? "policy"}";

        [Fact]
        public async Task BellApprove_SecondPerson_OwnRequest_403_AuditedDenied_NothingRecorded()
        {
            await using var rig = await Rig.StartAsync();
            var (alice, adam) = (await Rig.CallerAsync(rig, "alice"), await Rig.CallerAsync(rig, "adam"));
            Assert.Empty(await rig.ItemsAsync(adam));
            Assert.Empty(await rig.ItemsAsync(alice));
            var request = await rig.RequestAsync(alice);
            var mine = await rig.WaitItemAsync(alice, ObservabilityTypes.ActionApproval);
            await rig.WaitItemAsync(adam, ObservabilityTypes.ActionApproval);

            var refused = await rig.BellAsync(alice, mine.GetProperty("id").GetGuid(), "approve");

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("second person", await refused.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ActionRequestState.Pending, (await rig.RequestRowAsync(request)).State);
            Assert.Equal(0, await rig.CommandsAsync());
            Assert.Equal(0L, await rig.InteractionsAsync());
            var denied = Assert.Single(await rig.AuditAsync("action.request.approve"));
            Assert.Equal((AuditOutcome.Denied, "own_request", "inbox"), (denied.Outcome, denied.Metadata["reason"], denied.Metadata["via"]));
            // The item is still there to decide, for the other admin.
            Assert.Equal("open", (await rig.ItemsAsync(adam, ObservabilityTypes.ActionApproval)).Single().GetProperty("correlation").GetProperty("state").GetString(), ignoreCase: true);
        }

        [Fact]
        public async Task BellApprove_DemotedAdminTokenNowOperator_403Role_EvenWithRecordedAdminRole()
        {
            await using var rig = await Rig.StartAsync(claimsHeader: true);
            string Claims(string name, string roles) => $"sub=s-{name};preferred_username={name};roles={roles};platform_tenant={LocalStack.TenantKey}";
            async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string claims, object? body = null)
            {
                using var message = new HttpRequestMessage(method, path) { Content = body is null ? null : System.Net.Http.Json.JsonContent.Create(body) };
                message.Headers.Add("X-Test-Claims", claims);
                return await rig.Client.SendAsync(message, Ct);
            }
            // The admin signs in (their roles are recorded), and an operator asks.
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, Me("/counts"), Claims("sam", "admin"))).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{rig.Tenant}/agents/{rig.Agent}/action-requests", Claims("erin", "operator"),
                new { action = AgentActions.ServiceRestart, parameters = new { service = "nginx" }, reason = "stuck" })).StatusCode);
            JsonElement item = default;
            await ProvisionedHost.WaitAsync(() =>
            {
                var items = Caller.JsonAsync(SendAsync(HttpMethod.Get, Me(), Claims("sam", "admin")).GetAwaiter().GetResult()).GetAwaiter().GetResult();
                if (items.GetArrayLength() == 0) return false;
                item = items[0];
                return true;
            }, "sam's item");

            // The token now says operator: the recorded admin role and the item don't matter.
            var refused = await SendAsync(HttpMethod.Post, Me($"/{item.GetProperty("id").GetGuid()}/actions/approve"), Claims("sam", "operator"), new { });

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(0, await rig.CommandsAsync());
            Assert.Equal(0L, await rig.InteractionsAsync());
            var denied = Assert.Single(await rig.AuditAsync("action.request.approve"));
            Assert.Equal((AuditOutcome.Denied, "role", "inbox"), (denied.Outcome, denied.Metadata["reason"], denied.Metadata["via"]));
        }

        [Fact]
        public async Task BellApprove_InactiveTenantRow_403_NothingSigned()
        {
            await using var rig = await Rig.StartAsync();
            var (alice, erin) = (await Rig.CallerAsync(rig, "alice"), await Rig.CallerAsync(rig, "erin"));
            Assert.Empty(await rig.ItemsAsync(alice));
            var request = await rig.RequestAsync(erin);
            var item = await rig.WaitItemAsync(alice, ObservabilityTypes.ActionApproval);
            await using (var db = rig.Database.Context())
            {
                await db.Tenants.Where(t => t.Id == rig.Tenant).ExecuteUpdateAsync(t => t.SetProperty(x => x.Status, TenantStatus.Suspended), Ct);
            }

            var refused = await rig.BellAsync(alice, item.GetProperty("id").GetGuid(), "approve");

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal(0, await rig.CommandsAsync());
            Assert.Equal(ActionRequestState.Pending, (await rig.RequestRowAsync(request)).State);
            Assert.Equal(0L, await rig.InteractionsAsync());
        }

        [Fact]
        public async Task BellApprove_ExpiredNotYetSwept_409NotWaiting_RequestExpired_ItemsResolved()
        {
            await using var rig = await Rig.StartAsync(new() { ["Actions:SweepInterval"] = "00:10:00" });
            var (alice, adam, erin) = (await Rig.CallerAsync(rig, "alice"), await Rig.CallerAsync(rig, "adam"), await Rig.CallerAsync(rig, "erin"));
            Assert.Empty(await rig.ItemsAsync(alice));
            Assert.Empty(await rig.ItemsAsync(adam));
            var request = await rig.RequestAsync(erin);
            var item = await rig.WaitItemAsync(alice, ObservabilityTypes.ActionApproval);
            await rig.WaitItemAsync(adam, ObservabilityTypes.ActionApproval);
            await using (var db = rig.Database.Context())
            {
                await db.ActionRequests.Where(r => r.Id == request).ExecuteUpdateAsync(r => r.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct);
            }

            var refused = await rig.BellAsync(alice, item.GetProperty("id").GetGuid(), "approve");

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("Not waiting", await refused.Content.ReadAsStringAsync(Ct));
            Assert.Equal((ActionRequestState.Expired, 0), ((await rig.RequestRowAsync(request)).State, await rig.CommandsAsync()));
            Assert.Equal(0L, await rig.InteractionsAsync());
            // Closed for everyone, with the platform as who ended it.
            var resolved = await rig.WaitItemAsync(adam, ObservabilityTypes.ActionApproval, state: "resolved");
            Assert.Equal("Expired by platform", resolved.GetProperty("correlation").GetProperty("outcome").GetString());
        }

        [Fact]
        public async Task BellApprove_AgentRevoked_409()
        {
            await using var rig = await Rig.StartAsync();
            var (alice, erin) = (await Rig.CallerAsync(rig, "alice"), await Rig.CallerAsync(rig, "erin"));
            Assert.Empty(await rig.ItemsAsync(alice));
            var request = await rig.RequestAsync(erin);
            var item = await rig.WaitItemAsync(alice, ObservabilityTypes.ActionApproval);
            await using (var db = rig.Database.Context())
            {
                await db.Agents.Where(a => a.Id == rig.Agent).ExecuteUpdateAsync(a => a.SetProperty(x => x.Status, AgentStatus.Revoked), Ct);
            }

            var refused = await rig.BellAsync(alice, item.GetProperty("id").GetGuid(), "approve");

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("Agent revoked", await refused.Content.ReadAsStringAsync(Ct));
            Assert.Equal((ActionRequestState.Pending, 0, 0L), ((await rig.RequestRowAsync(request)).State, await rig.CommandsAsync(), await rig.InteractionsAsync()));
        }

        [Fact]
        public async Task BellApprove_StaleEvidence_409_FinalCancels()
        {
            await using var rig = await Rig.StartAsync();
            var (alice, adam, erin) = (await Rig.CallerAsync(rig, "alice"), await Rig.CallerAsync(rig, "adam"), await Rig.CallerAsync(rig, "erin"));
            Assert.Empty(await rig.ItemsAsync(alice));
            Assert.Empty(await rig.ItemsAsync(adam));
            var (alert, _) = await FireAsync(rig.Database);
            var request = await rig.RequestAsync(erin, alert: alert);
            var item = await rig.WaitItemAsync(alice, ObservabilityTypes.ActionApproval);
            await rig.WaitItemAsync(adam, ObservabilityTypes.ActionApproval);
            // The alert recovered while the request waited.
            await ResolveAsync(rig.Database, alert);

            var refused = await rig.BellAsync(alice, item.GetProperty("id").GetGuid(), "approve");

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            // The page's own refusal for evidence that no longer holds (a recovered alert: precondition failed; one too old: stale evidence).
            Assert.Matches("Precondition failed|Stale evidence", await refused.Content.ReadAsStringAsync(Ct));
            Assert.Equal((ActionRequestState.Cancelled, 0, 0L), ((await rig.RequestRowAsync(request)).State, await rig.CommandsAsync(), await rig.InteractionsAsync()));
            var resolved = await rig.WaitItemAsync(adam, ObservabilityTypes.ActionApproval, state: "resolved");
            Assert.Equal("Cancelled by platform", resolved.GetProperty("correlation").GetProperty("outcome").GetString());
        }

        [Fact]
        public async Task BellReject_FixedComment_AuditViaInbox()
        {
            await using var rig = await Rig.StartAsync();
            var (alice, erin) = (await Rig.CallerAsync(rig, "alice"), await Rig.CallerAsync(rig, "erin"));
            Assert.Empty(await rig.ItemsAsync(alice));
            var request = await rig.RequestAsync(erin);
            var item = await rig.WaitItemAsync(alice, ObservabilityTypes.ActionApproval);

            var rejected = await rig.BellAsync(alice, item.GetProperty("id").GetGuid(), "reject");

            Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
            var row = await rig.RequestRowAsync(request);
            Assert.Equal((ActionRequestState.Rejected, "alice", "Rejected from the inbox."), (row.State, row.DecidedBy, row.Comment));
            Assert.Equal(0, await rig.CommandsAsync());
            var audit = Assert.Single(await rig.AuditAsync("action.request.reject"));
            Assert.Equal((AuditOutcome.Succeeded, "inbox"), (audit.Outcome, audit.Metadata["via"]));
            var resolved = await rig.WaitItemAsync(alice, ObservabilityTypes.ActionApproval, state: "resolved");
            Assert.Equal("Rejected by alice", resolved.GetProperty("correlation").GetProperty("outcome").GetString());
        }

        // ─── Acknowledge ──────────────────────────────────────────────────────

        [Fact]
        public async Task BellAck_Alert_SamePath_CorrelationAcknowledged_AuditViaInbox_ViewerItem_403_ResolvedAlert_409()
        {
            await using var rig = await Rig.StartAsync(new() { ["Notifications:Inbox:Roles:0"] = "viewer", ["Notifications:Inbox:Roles:1"] = "operator" });
            var (erin, victor) = (await Rig.CallerAsync(rig, "erin"), await Rig.CallerAsync(rig, "victor"));
            Assert.Empty(await rig.ItemsAsync(erin));
            Assert.Empty(await rig.ItemsAsync(victor));
            // The alert escalates: a policy the platform selects and the pipeline has.
            await using (var policies = rig.Database.Context())
            {
                policies.EscalationPolicies.Add(new EscalationPolicy { TenantId = rig.Tenant, Key = "pager-a", Name = "pager-a", MinimumSeverity = AlertSeverity.Info, Enabled = true });
                await policies.SaveChangesAsync(Ct);
            }
            await PipelinePolicyAsync(rig.Factory, "pager-a");
            var (alert, firing) = await FireAsync(rig.Database);
            await PlanAsync(rig.Factory, rig.Database, firing);
            string EscalationSql(string column) => $"SELECT {column} FROM notifications.escalations e JOIN notifications.correlations c ON c.id = e.correlation_id AND c.tenant_id = e.tenant_id WHERE c.key = 'alert:{alert}'";
            await ProvisionedHost.WaitAsync(() => ScalarAsync(rig.Database, EscalationSql("count(*)")).GetAwaiter().GetResult() is long n && n == 1, "the escalation to start");
            Assert.Null(await ScalarAsync(rig.Database, EscalationSql("e.stop_reason")));
            var (operatorItem, viewerItem) = (await rig.WaitItemAsync(erin, ObservabilityTypes.AlertFiring), await rig.WaitItemAsync(victor, ObservabilityTypes.AlertFiring));

            // A viewer may see the alert, not acknowledge it: the page's 403, nothing recorded.
            Assert.Equal(HttpStatusCode.Forbidden, (await rig.BellAcknowledgeAsync(victor, viewerItem.GetProperty("id").GetGuid())).StatusCode);
            Assert.Equal(0L, await rig.InteractionsAsync());
            Assert.Equal(AuditOutcome.Denied, Assert.Single(await rig.AuditAsync("alert.acknowledge")).Outcome);

            Assert.Equal(HttpStatusCode.OK, (await rig.BellAcknowledgeAsync(erin, operatorItem.GetProperty("id").GetGuid())).StatusCode);

            await using (var db = rig.Database.Context())
            {
                var row = await db.Alerts.AsNoTracking().SingleAsync(a => a.Id == alert, Ct);
                Assert.Equal("erin", row.AcknowledgedBy);
            }
            var audits = await rig.AuditAsync("alert.acknowledge");
            Assert.Equal((AuditOutcome.Succeeded, "inbox"), (audits[^1].Outcome, audits[^1].Metadata["via"]));
            var acknowledged = await rig.WaitItemAsync(erin, ObservabilityTypes.AlertFiring, state: "acknowledged");
            Assert.Equal("erin", acknowledged.GetProperty("correlation").GetProperty("acknowledgedBy").GetString());
            Assert.Equal("acknowledged", (await CorrelationStateAsync(rig.Database, $"alert:{alert}"))?.ToLowerInvariant());
            // The escalation stopped: no step is left to page an alert someone took.
            await ProvisionedHost.WaitAsync(() => ScalarAsync(rig.Database, EscalationSql("e.stop_reason")).GetAwaiter().GetResult() is not null, "the escalation to stop");
            Assert.Null(await ScalarAsync(rig.Database, EscalationSql("e.next_at")));

            // Resolved: the item is closed, and a click on it records nothing.
            await PlanAsync(rig.Factory, rig.Database, await ResolveAsync(rig.Database, alert));
            await rig.WaitItemAsync(victor, ObservabilityTypes.AlertFiring, state: "resolved");
            var before = await rig.InteractionsAsync();
            Assert.Equal(HttpStatusCode.Conflict, (await rig.BellAcknowledgeAsync(victor, viewerItem.GetProperty("id").GetGuid())).StatusCode);
            Assert.Equal(before, await rig.InteractionsAsync());
        }

        // ─── The handler's own gates ──────────────────────────────────────────

        [Theory]
        [InlineData(ObservabilityTypes.ActionApproval, "approval:0193f0aa-0000-7000-8000-0000000000a1", true)]
        [InlineData(ObservabilityTypes.AlertFiring, "alert:0193f0aa-0000-7000-8000-0000000000a1", true)]
        [InlineData(ObservabilityTypes.ActionApproval, "APPROVAL:0193f0aa-0000-7000-8000-0000000000a1", false)]
        [InlineData(ObservabilityTypes.ActionApproval, "approval:0193F0AA-0000-7000-8000-0000000000A1", false)]
        [InlineData(ObservabilityTypes.ActionApproval, "approval: 0193f0aa-0000-7000-8000-0000000000a1", false)]
        [InlineData(ObservabilityTypes.ActionApproval, "approval:0193f0aa-0000-7000-8000-0000000000a1\n", false)]
        [InlineData(ObservabilityTypes.ActionApproval, "approval:0193f0aa00007000800000000000a1", false)]
        [InlineData(ObservabilityTypes.ActionApproval, "approval:{0193f0aa-0000-7000-8000-0000000000a1}", false)]
        [InlineData(ObservabilityTypes.ActionApproval, "alert:0193f0aa-0000-7000-8000-0000000000a1", false)]
        [InlineData(ObservabilityTypes.AlertFiring, "approval:0193f0aa-0000-7000-8000-0000000000a1", false)]
        [InlineData("observability.alert.resolved", "alert:0193f0aa-0000-7000-8000-0000000000a1", false)]
        [InlineData(ObservabilityTypes.ActionApproval, null, false)]
        public void Handler_CorrelationKeyNonCanonical_NotParsed_409Unhandled(string type, string? key, bool parsed)
            => Assert.Equal(parsed, ObservabilityInboxInteractions.TryParse(type, key, out _));

        [Fact]
        public async Task Handler_NonCanonicalKey_AnswersInteractionUnhandled_409()
        {
            await using var rig = await Rig.StartAsync();
            await using var scope = rig.Factory.Services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetServices<Juice.Notifications.Inbox.IInboxInteractionHandler>().OfType<ObservabilityInboxInteractions>().Single();
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
            http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new(scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value.TenantClaim, LocalStack.TenantKey),
                    new("roles", "admin"), new("preferred_username", "alice")], "test", "preferred_username", "roles"));

            var interaction = new Juice.Notifications.Inbox.InboxInteraction(Tenant, Guid.NewGuid(), Guid.NewGuid(), ObservabilityTypes.ActionApproval, "APPROVAL:" + Guid.NewGuid(),
                Juice.Notifications.Inbox.InteractionKinds.Action, "approve");
            var result = await handler.DecideAsync(interaction, http, Ct);

            Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
            Assert.Equal(409, ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result!).StatusCode);
            Assert.Equal(0L, await rig.InteractionsAsync());
            Assert.Equal(0, await rig.CommandsAsync());
        }

        [Fact]
        public async Task InboxOff_ActionsAndAcknowledge_404()
        {
            await using var rig = await Rig.StartAsync(new() { ["Notifications:Inbox:Enabled"] = "false" });
            var alice = await Rig.CallerAsync(rig, "alice");
            var item = Guid.NewGuid();

            // Nothing is mapped: the Console's fallback route takes the path and answers a POST with 405 (a GET with 404), as it does the hub's.
            var expected = new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed };
            Assert.Contains((await rig.BellAsync(alice, item, "approve")).StatusCode, expected);
            Assert.Contains((await rig.BellAcknowledgeAsync(alice, item)).StatusCode, expected);
            Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync(Me($"/{item}"))).StatusCode);
            Assert.Equal((0, 0L), (await rig.CommandsAsync(), await rig.InteractionsAsync()));
        }

        [Fact]
        public async Task InboxAction_Logs_NoAuthorizationHeaderOrAccessToken()
        {
            await using var rig = await Rig.StartAsync();
            var (alice, erin) = (await Rig.CallerAsync(rig, "alice"), await Rig.CallerAsync(rig, "erin"));
            var token = await LocalStack.TokenAsync("alice");
            Assert.Empty(await rig.ItemsAsync(alice));
            await rig.RequestAsync(erin);
            var item = await rig.WaitItemAsync(alice, ObservabilityTypes.ActionApproval);

            using var request = new HttpRequestMessage(HttpMethod.Post, Me($"/{item.GetProperty("id").GetGuid()}/actions/approve?access_token={Uri.EscapeDataString(token)}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = System.Net.Http.Json.JsonContent.Create(new { });
            using var response = await rig.Client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(rig.Logs.Entries, e => e.Message.Contains(token, StringComparison.Ordinal) || e.Message.Contains(token.Split('.')[2], StringComparison.Ordinal));
            Assert.DoesNotContain(rig.Logs.Entries, e => e.Message.Contains("Bearer ", StringComparison.Ordinal));
        }

        // ─── Races ────────────────────────────────────────────────────────────

        [Fact]
        public async Task Inbox_TwoBellsAndAPage_AtOnce_OneDecision_OneCommand_OneAuditRow_LosersRefused()
        {
            await using var rig = await Rig.StartAsync();
            var (alice, adam, erin) = (await Rig.CallerAsync(rig, "alice"), await Rig.CallerAsync(rig, "adam"), await Rig.CallerAsync(rig, "erin"));
            var carol = await Rig.CallerAsync(rig, "alice");
            Assert.Empty(await rig.ItemsAsync(alice));
            Assert.Empty(await rig.ItemsAsync(adam));
            HashSet<Guid> seenA = [], seenB = [];
            for (var i = 0; i < 10; i++)
            {
                var request = await rig.RequestAsync(erin, $"race-{i}");
                var (a, b) = (await rig.WaitItemAsync(alice, ObservabilityTypes.ActionApproval, except: seenA), await rig.WaitItemAsync(adam, ObservabilityTypes.ActionApproval, except: seenB));
                seenA.Add(a.GetProperty("id").GetGuid());
                seenB.Add(b.GetProperty("id").GetGuid());
                var (commands, audits) = (await rig.CommandsAsync(), (await rig.AuditAsync("action.request.approve")).Count + (await rig.AuditAsync("action.request.reject")).Count);

                // Admin X's bell approves, admin Y's bell rejects, and the page approves, all at once.
                var results = await Task.WhenAll(rig.BellAsync(alice, a.GetProperty("id").GetGuid(), "approve"), rig.BellAsync(adam, b.GetProperty("id").GetGuid(), "reject"),
                    carol.PostAsync($"/api/v1/tenants/{rig.Tenant}/action-requests/{request}/approval", new { comment = "page" }));

                Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
                Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Conflict }));
                var state = (await rig.RequestRowAsync(request)).State;
                Assert.Contains(state, new[] { ActionRequestState.Issued, ActionRequestState.Rejected });
                Assert.Equal(commands + (state == ActionRequestState.Issued ? 1 : 0), await rig.CommandsAsync());
                // One decision, one audit row: the losers find the request decided and write none.
                var after = (await rig.AuditAsync("action.request.approve")).Count + (await rig.AuditAsync("action.request.reject")).Count;
                Assert.Equal(audits + 1, after);
            }
        }

        [Fact]
        public async Task BellAck_AlreadyAcknowledgedAlert_WritesNoSecondAuditRow()
        {
            await using var rig = await Rig.StartAsync();
            var (erin, alice) = (await Rig.CallerAsync(rig, "erin"), await Rig.CallerAsync(rig, "alice"));
            Assert.Empty(await rig.ItemsAsync(erin));
            Assert.Empty(await rig.ItemsAsync(alice));
            var (alert, firing) = await FireAsync(rig.Database);
            await PlanAsync(rig.Factory, rig.Database, firing);
            var item = await rig.WaitItemAsync(erin, ObservabilityTypes.AlertFiring);
            var other = await rig.WaitItemAsync(alice, ObservabilityTypes.AlertFiring);

            Assert.Equal(HttpStatusCode.OK, (await rig.BellAcknowledgeAsync(erin, item.GetProperty("id").GetGuid())).StatusCode);
            var first = (await rig.AuditAsync("alert.acknowledge")).Count;
            // Another person's item for the same alert: the alert is acknowledged already, so the page's path writes nothing more.
            var again = await rig.BellAcknowledgeAsync(alice, other.GetProperty("id").GetGuid());

            Assert.True(again.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict);
            Assert.Equal((1, first), (first, (await rig.AuditAsync("alert.acknowledge")).Count));
            _ = alert;
        }

        [Fact]
        public async Task Inbox_BellAndPage_ApproveSameMoment_OneCommand_LoserGets409()
        {
            await using var rig = await Rig.StartAsync();
            var (alice, adam, erin) = (await Rig.CallerAsync(rig, "alice"), await Rig.CallerAsync(rig, "adam"), await Rig.CallerAsync(rig, "erin"));
            Assert.Empty(await rig.ItemsAsync(alice));
            var seen = new HashSet<Guid>();
            for (var i = 0; i < 20; i++)
            {
                var request = await rig.RequestAsync(erin, $"svc-{i}");
                var item = (await rig.WaitItemAsync(alice, ObservabilityTypes.ActionApproval, except: seen)).GetProperty("id").GetGuid();
                seen.Add(item);
                var before = await rig.CommandsAsync();

                var bell = rig.BellAsync(alice, item, "approve");
                var page = adam.PostAsync($"/api/v1/tenants/{rig.Tenant}/action-requests/{request}/approval", new { comment = "page" });
                var statuses = (await Task.WhenAll(bell, page)).Select(r => r.StatusCode).Order().ToList();

                Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], statuses);
                Assert.Equal(before + 1, await rig.CommandsAsync());
                Assert.Equal(ActionRequestState.Issued, (await rig.RequestRowAsync(request)).State);
            }
        }

        // ─── The catalogue ────────────────────────────────────────────────────

        [Fact]
        public async Task ActionDto_DryRunApproval_FollowsOverrides()
        {
            await using var rig = await Rig.StartAsync(new() { ["Actions:Overrides:files.cleanup"] = "SecondPerson" });
            await using var plain = await Rig.StartAsync();
            async Task<Dictionary<string, JsonElement>> CatalogueAsync(Rig r)
            {
                var caller = await Rig.CallerAsync(r, "victor");
                var list = await Caller.JsonAsync(await caller.GetAsync($"/api/v1/tenants/{r.Tenant}/agents/{r.Agent}/actions"));
                return list.GetProperty("items").EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);
            }

            var defaults = await CatalogueAsync(plain);
            // A dry run changes nothing, so it is Low (no approval) while a real run is Medium (an admin); an action with no dryRun says nothing of it.
            Assert.Equal(("Medium", "Admin", "None"), (defaults["files.cleanup"].GetProperty("risk").GetString(), defaults["files.cleanup"].GetProperty("approval").GetString(),
                defaults["files.cleanup"].GetProperty("dryRunApproval").GetString()));
            Assert.False(defaults["service.restart"].TryGetProperty("dryRunApproval", out var none) && none.ValueKind != JsonValueKind.Null);

            // An override names the action: it governs the dry run as it does the real one.
            var overridden = await CatalogueAsync(rig);
            Assert.Equal(("SecondPerson", "SecondPerson"), (overridden["files.cleanup"].GetProperty("approval").GetString(), overridden["files.cleanup"].GetProperty("dryRunApproval").GetString()));
        }
    }
}
