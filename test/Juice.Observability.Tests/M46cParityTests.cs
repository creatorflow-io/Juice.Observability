using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.MediatR;
using Juice.Notifications;
using Juice.Notifications.Configuration;
using Juice.Notifications.Data;
using Juice.Notifications.Rendering;
using Juice.Notifications.Types;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Juice.Observability.Notifications.Migration;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ObsManagedBy = Juice.Observability.Data.ManagedBy;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// M46c slice B: the alias keeps legacy's ownership when the file drops an endpoint (the alias's configuration rows are handed back to the
    /// alias's API, disabled), and the bodies the new pipeline renders from the declared templates are legacy's, byte for byte.
    /// </summary>
    [Trait("Milestone", "M46c")]
    public sealed class M46cParityTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private const string Secret = "env:JUICE_WEBHOOK_FILE";

        private static Dictionary<string, string> FileSettings(string pipeline, params string[] keys)
        {
            var settings = new Dictionary<string, string>
            {
                ["Webhooks:Smtp:Host"] = "smtp.example.com",
                ["Webhooks:Smtp:From"] = "alerts@example.com",
                ["Notifications:Delivery:SmtpServers:observability:Host"] = "smtp.example.com",
                ["Notifications:Migration:SmtpServer"] = "observability",
                ["Notifications:Pipeline"] = pipeline,
            };
            for (var i = 0; i < keys.Length; i++)
            {
                settings[$"Webhooks:Endpoints:{i}:Key"] = keys[i];
                settings[$"Webhooks:Endpoints:{i}:Kind"] = "Webhook";
                settings[$"Webhooks:Endpoints:{i}:Url"] = $"https://receiver.example/hooks/{keys[i]}";
                settings[$"Webhooks:Endpoints:{i}:SecretRef"] = Secret;
            }
            return settings;
        }

        private sealed record Rows(string? IntegrationOwner, bool? IntegrationEnabled, string? RouteOwner, bool? RouteEnabled, string[] TargetOwners, bool TargetsEnabled);

        private static async Task<Rows> RowsAsync(ApiTests.ApiFactory factory, string key)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var integration = await notifications.Integrations.AsNoTracking().SingleOrDefaultAsync(i => i.Key == key, Ct);
            var route = await notifications.Routes.AsNoTracking().SingleOrDefaultAsync(r => r.Key == key, Ct);
            var targets = integration is null ? [] : await notifications.IntegrationTargets.AsNoTracking().Where(t => t.IntegrationId == integration.Id).ToListAsync(Ct);
            return new Rows(integration?.ManagedBy, integration?.Enabled, route?.ManagedBy, route?.Enabled, [.. targets.Select(t => t.ManagedBy).Distinct()], targets.Any(t => t.Enabled));
        }

        /// <summary>
        /// A handed-back endpoint: the alias's, with its route stopped. Where legacy sends the mirror writes its legacy row straight after (the integration and
        /// targets as the alias writes them); in new nothing does, and the hand-back's own disabled rows stand.
        /// </summary>
        private static Rows HandedBack(bool mirrored) => new(AliasVocabulary.WebhooksAlias, mirrored, AliasVocabulary.WebhooksAlias, false, [AliasVocabulary.WebhooksAlias], mirrored);
        private static readonly Rows FromFile = new(AliasVocabulary.AliasConfig, true, AliasVocabulary.AliasConfig, true, [AliasVocabulary.AliasConfig], true);

        private static string Describe(Rows r) => $"integration {r.IntegrationOwner}/{r.IntegrationEnabled}, route {r.RouteOwner}/{r.RouteEnabled}, targets [{string.Join(",", r.TargetOwners)}]/{r.TargetsEnabled}";

        private static void AssertRows(Rows expected, Rows actual) => Assert.Equal(Describe(expected), Describe(actual));

        /// <summary>The file declares <c>from-file</c>; the next start's file doesn't. Returns the second host, after its start.</summary>
        private static async Task<ApiTests.ApiFactory> DropAsync(ScratchDatabase database, string pipeline)
        {
            await using (var first = new ApiTests.ApiFactory(database, settings: FileSettings(pipeline, "from-file")))
            {
                _ = first.CreateClient();
                AssertRows(FromFile, await RowsAsync(first, "from-file"));
            }
            var second = new ApiTests.ApiFactory(database, settings: FileSettings(pipeline));
            _ = second.CreateClient();
            return second;
        }

        // ─── Ownership ────────────────────────────────────────────────────────

        /// <summary>
        /// In legacy the endpoint the file drops goes back to the API, disabled, as legacy always did; the alias's copy is handed back the same
        /// way (webhooks-alias, disabled), so an admin's enable through /webhooks is accepted by both sides.
        /// </summary>
        [Fact]
        public async Task Legacy_FileDropsEndpoint_LegacyApiDisabled_AliasCopyHandedBackDisabled()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var second = await DropAsync(database, "legacy");
            var (_, _, admin, tenant) = await Caller.AllAsync(second.CreateClient(), database);
            await using var db = database.Context();
            var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Key == "from-file", Ct);
            Assert.Equal((ObsManagedBy.Api, false), (endpoint.ManagedBy, endpoint.Enabled));
            AssertRows(HandedBack(true), await RowsAsync(second, "from-file"));

            var put = await admin.PutAsync($"/api/v1/tenants/{tenant}/webhooks/{endpoint.Id}", new { secretRef = Secret, enabled = true });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            Assert.True((await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == endpoint.Id, Ct)).Enabled);
            var rows = await RowsAsync(second, "from-file");
            Assert.Equal((AliasVocabulary.WebhooksAlias, true), (rows.RouteOwner, rows.RouteEnabled));
        }

        [Fact]
        public async Task Shadow_FileDropsEndpoint_SameAsLegacy()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var second = await DropAsync(database, "shadow");
            var (_, _, admin, tenant) = await Caller.AllAsync(second.CreateClient(), database);
            await using var db = database.Context();
            var endpoint = await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Key == "from-file", Ct);
            Assert.Equal((ObsManagedBy.Api, false), (endpoint.ManagedBy, endpoint.Enabled));
            AssertRows(HandedBack(true), await RowsAsync(second, "from-file"));

            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/v1/tenants/{tenant}/webhooks/{endpoint.Id}", new { secretRef = Secret, enabled = true })).StatusCode);
            Assert.True((await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Id == endpoint.Id, Ct)).Enabled);
        }

        /// <summary>In new there is no legacy row: the alias's copy is the endpoint, now the API's, disabled, and an admin edits it through /webhooks.</summary>
        [Fact]
        public async Task New_FileDropsEndpoint_HandedBackDisabled_EditableThroughAliasApi()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var second = await DropAsync(database, "new");
            var (_, _, admin, tenant) = await Caller.AllAsync(second.CreateClient(), database);
            AssertRows(HandedBack(false), await RowsAsync(second, "from-file"));
            Guid id;
            await using (var scope = second.Services.CreateAsyncScope())
            {
                id = (await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Integrations.AsNoTracking().SingleAsync(i => i.Key == "from-file", Ct)).Id;
            }
            var webhooks = $"/api/v1/tenants/{tenant}/webhooks";
            var listed = await Caller.JsonAsync(await admin.GetAsync(webhooks));
            Assert.Equal(ObsManagedBy.Api, Assert.Single(listed.GetProperty("items").EnumerateArray()).GetProperty("managedBy").GetString());

            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"{webhooks}/{id}", new { secretRef = Secret, enabled = true })).StatusCode);

            var rows = await RowsAsync(second, "from-file");
            Assert.Equal((AliasVocabulary.WebhooksAlias, true, AliasVocabulary.WebhooksAlias, true), (rows.IntegrationOwner, rows.IntegrationEnabled, rows.RouteOwner, rows.RouteEnabled));
        }

        /// <summary>In new, an endpoint the file declares is the file's: the admin's PUT through /webhooks and the alias's own write both get managed_by_configuration (409).</summary>
        [Fact]
        public async Task New_ConfigEndpoint_AdminPutAndAliasPut_409ManagedByConfiguration()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: FileSettings("new", "from-file"));
            var (_, _, admin, tenant) = await Caller.AllAsync(factory.CreateClient(), database);
            var id = await RowsIdAsync(factory, "from-file");

            var put = await admin.PutAsync($"/api/v1/tenants/{tenant}/webhooks/{id}", new { secretRef = Secret, enabled = false });
            Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);

            await using var scope = factory.Services.CreateAsyncScope();
            using var context = NotificationsMessageContext.Begin("test");
            var outcome = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(
                new PutRoute(Guid.Parse(LocalStack.TenantKey), "from-file", null, null, false) { ManagedBy = AliasVocabulary.WebhooksAlias }, Ct);
            Assert.Equal(ErrorCodes.ManagedByConfiguration, outcome.Code);
            Assert.Equal(AliasVocabulary.AliasConfig, (await RowsAsync(factory, "from-file")).RouteOwner);
        }

        private static async Task<Guid> RowsIdAsync(ApiTests.ApiFactory factory, string key)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Integrations.AsNoTracking().SingleAsync(i => i.Key == key, Ct)).Id;
        }

        /// <summary>The state the interim (8781935) left: legacy's row the API's and disabled, the alias's rows still the file's with the route stopped.</summary>
        [Fact]
        public async Task Upgrade_InterimRows_RouteStoppedAliasConfig_HandedBackOnNextStart()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using (var first = new ApiTests.ApiFactory(database, settings: FileSettings("legacy", "from-file")))
            {
                _ = first.CreateClient();
                await using var scope = first.Services.CreateAsyncScope();
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                await notifications.Routes.Where(r => r.Key == "from-file").ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, false), Ct);
            }
            await using (var db = database.Context())
            {
                await db.WebhookEndpoints.Where(e => e.Key == "from-file").ExecuteUpdateAsync(s => s.SetProperty(e => e.ManagedBy, ObsManagedBy.Api).SetProperty(e => e.Enabled, false), Ct);
            }

            await using var second = new ApiTests.ApiFactory(database, settings: FileSettings("legacy"));
            _ = second.CreateClient();

            AssertRows(HandedBack(true), await RowsAsync(second, "from-file"));
        }

        [Fact]
        public async Task FileReAddsHandedBackEndpoint_TakenOverAsAliasConfig()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var pipeline = LocalStack.NotificationsPipeline ?? "legacy";
            await using (var dropped = await DropAsync(database, pipeline))
            {
                AssertRows(HandedBack(pipeline != "new"), await RowsAsync(dropped, "from-file"));
            }

            await using var third = new ApiTests.ApiFactory(database, settings: FileSettings(pipeline, "from-file"));
            _ = third.CreateClient();

            var rows = await RowsAsync(third, "from-file");
            Assert.Equal((AliasVocabulary.AliasConfig, AliasVocabulary.AliasConfig), (rows.IntegrationOwner, rows.RouteOwner));
        }

        /// <summary>The pipeline didn't start: the alias leaves its rows, and legacy stays authoritative (its row the API's, disabled).</summary>
        [Fact]
        public async Task PipelineNotStarted_NoHandBack_LegacyAuthoritative()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using (var first = new ApiTests.ApiFactory(database, settings: FileSettings("legacy", "from-file")))
            {
                _ = first.CreateClient();
            }
            var file = Path.Combine(Path.GetTempPath(), $"juice-obs-m46c-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(file, $$"""{ "tenants": { "{{LocalStack.TenantKey}}": { "integrations": { "mail": { "channel": "email", "kind": "smtp", "settings": { "server": "none" } } } } } }""", Ct);
            try
            {
                var settings = FileSettings("legacy");
                settings["Notifications:Provisioning:Path"] = file;
                await using var second = new ApiTests.ApiFactory(database, settings: settings);
                _ = second.Server;
                Assert.NotNull(((PipelineStart)second.Services.GetService(typeof(PipelineStart))!).Failure);

                AssertRows(FromFile, await RowsAsync(second, "from-file"));
                await using var db = database.Context();
                var legacy = await db.WebhookEndpoints.AsNoTracking().SingleAsync(e => e.Key == "from-file", Ct);
                Assert.Equal((ObsManagedBy.Api, false), (legacy.ManagedBy, legacy.Enabled));
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>The migration command leaves what the file declares to the provisioner: informational, exit 0 (not a conflict), and the alias's rows stay the file's.</summary>
        [Fact]
        public async Task MigrationCommand_FileEndpoints_AreInformational_Exit0()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var settings = FileSettings("legacy", "from-file");
            settings["Webhooks:Allowlist:0:Destination"] = "10.40.0.0/16";
            await using var factory = new ApiTests.ApiFactory(database, settings: settings);
            _ = factory.CreateClient();
            var output = new StringWriter();

            var reported = await new Api.NotificationsMigrationCommand(false, null).RunAsync(factory.Services, output, Ct);
            var applied = await new Api.NotificationsMigrationCommand(true, null).RunAsync(factory.Services, output, Ct);

            Assert.Equal((0, 0), (reported, applied));
            Assert.Contains("owned_by_configuration", output.ToString());
            Assert.DoesNotContain("\"conflict\"", output.ToString());
            AssertRows(FromFile, await RowsAsync(factory, "from-file"));
        }

        /// <summary>A file endpoint the provisioner hasn't written yet (the pipeline was down at start) has its own note, and isn't migrating: a policy naming it would route to nobody.</summary>
        [Fact]
        public async Task MigrationCommand_FileEndpoint_NoRowsYet_IsNotYetWritten_NotMigrating()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: FileSettings("legacy", "from-file"));
            _ = factory.CreateClient();
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                var integration = await notifications.Integrations.SingleAsync(i => i.Key == "from-file", Ct);
                await notifications.Routes.Where(r => r.Key == "from-file").ExecuteDeleteAsync(Ct);
                await notifications.IntegrationTargets.Where(t => t.IntegrationId == integration.Id).ExecuteDeleteAsync(Ct);
                await notifications.Integrations.Where(i => i.Id == integration.Id).ExecuteDeleteAsync(Ct);
            }
            var output = new StringWriter();

            var reported = await new Api.NotificationsMigrationCommand(false, null).RunAsync(factory.Services, output, Ct);

            Assert.Equal(0, reported);
            Assert.Contains("owned_by_configuration_not_yet_written", output.ToString());
            Assert.DoesNotContain("\"owned_by_configuration\"", output.ToString());
            Assert.DoesNotContain("\"conflict\"", output.ToString());
        }

        private static async Task<(int Exit, Dictionary<string, (string Action, string Problems)> Items)> ReportAsync(ApiTests.ApiFactory factory, bool apply)
        {
            var output = new StringWriter();
            var exit = await new Api.NotificationsMigrationCommand(apply, null).RunAsync(factory.Services, output, Ct);
            using var json = JsonDocument.Parse(output.ToString());
            var items = json.RootElement.GetProperty("items").EnumerateArray().ToDictionary(
                i => i.GetProperty("key").GetString()!,
                i => (i.GetProperty("action").GetString()!, i.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("code").GetString()!).Order().Aggregate("", (a, c) => a + c + ";")));
            return (exit, items);
        }

        /// <summary>A policy naming a file endpoint: it follows the endpoint once the provisioner wrote it, and is a conflict (and not written) while it hasn't.</summary>
        [Fact]
        public async Task MigrationCommand_PolicyNamingFileEndpoint_FollowsWhetherItsRowsAreWritten()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: FileSettings("legacy", "from-file"));
            _ = factory.CreateClient();
            var tenant = await ProducerAdapterTests.TenantAsync(database);
            await using (var db = database.Context())
            {
                db.EscalationPolicies.Add(new Juice.Observability.Data.EscalationPolicy { TenantId = tenant, Key = "paging", Name = "Paging", Steps = [new() { AfterMinutes = 5, EndpointKeys = ["from-file"] }] });
                await db.SaveChangesAsync(Ct);
            }

            var (written, items) = await ReportAsync(factory, apply: false);
            Assert.Equal(0, written);
            Assert.Equal(("unchanged", ProblemCodes.OwnedByConfiguration + ";"), items["from-file"]);
            Assert.Equal("create", items["paging"].Action);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                var integration = await notifications.Integrations.SingleAsync(i => i.Key == "from-file", Ct);
                await notifications.Routes.Where(r => r.Key == "from-file").ExecuteDeleteAsync(Ct);
                await notifications.IntegrationTargets.Where(t => t.IntegrationId == integration.Id).ExecuteDeleteAsync(Ct);
                await notifications.Integrations.Where(i => i.Id == integration.Id).ExecuteDeleteAsync(Ct);
            }

            foreach (var apply in new[] { false, true })
            {
                var (exit, missing) = await ReportAsync(factory, apply);
                Assert.Equal(2, exit);
                Assert.Equal(("unchanged", ProblemCodes.OwnedByConfigurationNotYetWritten + ";"), missing["from-file"]);
                Assert.Equal(("conflict", ProblemCodes.UnknownEndpoint + ";"), missing["paging"]);
            }
            await using var after = factory.Services.CreateAsyncScope();
            Assert.False(await after.ServiceProvider.GetRequiredService<NotificationsDbContext>().EscalationPolicies.AnyAsync(p => p.Key == "paging", Ct));
        }

        // ─── The hand-back meeting a concurrent change ────────────────────────

        private sealed class Logs : ILoggerProvider
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = [];
            public ILogger CreateLogger(string categoryName) => new Sink(this);
            public void Dispose() { }
            private sealed class Sink(Logs logs) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => true;
                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    lock (logs.Entries) logs.Entries.Add((logLevel, formatter(state, exception)));
                }
            }
        }

        /// <summary>
        /// Stands for another replica: after the Nth hand-back command (in this host's run) has loaded its rows and before they are saved, the
        /// integration of that command changes owner behind it, so the one save fails as notifications' concurrent_change.
        /// </summary>
        private sealed class InterferingMediator(IMediator inner, IServiceScopeFactory scopes, Func<int, bool> interfere, int[] sent) : IMediator
        {
            public ValueTask Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => inner.Send(request, cancellationToken);

            public async ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            {
                var response = await inner.Send(request, cancellationToken);
                if (request is HandBackAliasConfig handBack && interfere(Interlocked.Increment(ref sent[0])))
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                    await notifications.Integrations.IgnoreQueryFilters().Where(i => i.Key == handBack.IntegrationKey)
                        .ExecuteUpdateAsync(s => s.SetProperty(i => i.ManagedBy, AliasVocabulary.WebhooksAlias), cancellationToken);
                }
                return response;
            }

            public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => inner.CreateStream(request, cancellationToken);

            public ValueTask Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification
                => inner.Publish(notification, cancellationToken);
        }

        private static Action<IServiceCollection> Interfering(Logs logs, Func<int, bool> interfere)
        {
            var sent = new int[1];
            return services =>
            {
                var original = services.Last(d => d.ServiceType == typeof(IMediator));
                services.Remove(original);
                services.Add(new ServiceDescriptor(typeof(IMediator), sp => new InterferingMediator(
                    (IMediator)(original.ImplementationFactory?.Invoke(sp) ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)),
                    sp.GetRequiredService<IServiceScopeFactory>(), interfere, sent), original.Lifetime));
                services.AddLogging(b => b.AddProvider(logs));
            };
        }

        /// <summary>Two endpoints the file drops; a change of owner meets the first one's hand-back, once: the provisioner plans again and finishes.</summary>
        [Fact]
        public async Task HandBack_ConcurrentChange_ReplannedOnce_ThenLogged_PlatformStarts()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using (var first = new ApiTests.ApiFactory(database, settings: FileSettings("legacy", "a-file", "b-file")))
            {
                _ = first.CreateClient();
            }
            var once = new Logs();
            await using (var second = new ApiTests.ApiFactory(database, settings: FileSettings("legacy"), services: Interfering(once, n => n == 1)))
            {
                using var client = second.CreateClient();
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", Ct)).StatusCode);
                // The first command's save failed and nothing moved; the second pass found "a-file" the alias's already and moved "b-file".
                Assert.Equal(AliasVocabulary.WebhooksAlias, (await RowsAsync(second, "a-file")).IntegrationOwner);
                AssertRows(HandedBack(true), await RowsAsync(second, "b-file"));
                Assert.DoesNotContain(once.Entries, e => e.Level >= LogLevel.Warning && e.Message.Contains("still changing owner"));
                Assert.Contains(once.Entries, e => e.Message.Contains("planning again"));
            }

            // Reset: a database where both are the file's again, and the change meets the second pass as well: logged, the platform starts.
            await using var database2 = await LocalStack.CreateDatabaseAsync();
            await using (var first = new ApiTests.ApiFactory(database2, settings: FileSettings("legacy", "a-file", "b-file")))
            {
                _ = first.CreateClient();
            }
            var twice = new Logs();
            await using var third = new ApiTests.ApiFactory(database2, settings: FileSettings("legacy"), services: Interfering(twice, n => n is 1 or 3));
            using var started = third.CreateClient();
            Assert.Equal(HttpStatusCode.OK, (await started.GetAsync("/health/live", Ct)).StatusCode);
            Assert.Contains(twice.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("still changing owner"));
            // Nothing of ours was moved (the failed save rolled back): the route and the targets are the file's still; only the integration changed
            // owner, behind us, as the other replica's hand-back would have.
            var b = await RowsAsync(third, "b-file");
            Assert.Equal((AliasVocabulary.AliasConfig, AliasVocabulary.AliasConfig), (b.RouteOwner, string.Join(",", b.TargetOwners)));
        }

        // ─── Bodies ───────────────────────────────────────────────────────────

        private static (Rule Rule, Alert Alert, AlertTransition Transition) Alert(AlertState state, string subject, string ruleName, double? value, string reason, Dictionary<string, string>? evidence = null)
        {
            // A zone ahead of UTC: its "+07:00" is the "+" the template must not escape.
            var at = new DateTimeOffset(2026, 11, 26, 9, 30, 15, 123, TimeSpan.FromHours(7)).AddTicks(4567);
            var rule = new Rule { TenantId = Guid.Parse(LocalStack.TenantKey), Key = "orders-errors", Name = ruleName, Definition = "{}", DefinitionHash = "h" };
            var alert = new Alert
            {
                TenantId = rule.TenantId, RuleId = rule.Id, DedupKey = "d", SubjectKey = subject, Severity = AlertSeverity.Critical, State = state,
                BreachStartedAt = at.AddMinutes(-3), FiredAt = at, ResolvedAt = state == AlertState.Resolved ? at.AddMinutes(20) : null, Evidence = evidence ?? [],
            };
            return (rule, alert, new AlertTransition { TenantId = rule.TenantId, AlertId = alert.Id, ToState = state, At = alert.ResolvedAt ?? at, Value = value, Reason = reason });
        }

        public static TheoryData<string, string, string, double?, string> Variants => new()
        {
            { NotificationPlan.Firing, "service:shop/orders@production", "Orders errors", 0.25, "error rate 25% over 5%" },
            { NotificationPlan.Resolved, "service:shop/orders@production", "Orders errors", 0.01, "recovered" },
            { NotificationPlan.Escalated, "service:shop/orders@production", "Orders errors", null, "error rate 25% over 5%" },
            { NotificationPlan.Firing, "service:<a href=\"x\">&'é" + (char)0x2028 + "" + new string('x', 400) + "@prod", "Errors\r\nBcc: x\t+ a \\ b", 123456789.123456789, "a+b\tc\\d\"" },
            // Control characters other than line breaks and tabs, and the length at which legacy cuts (300 stays, 301 is cut).
            { NotificationPlan.Firing, "service:a\u000Bb\u0085c\u007Fd", "Orders errors", 0.25, "x" },
            { NotificationPlan.Firing, "s" + new string('y', 299), "Orders errors", 0.25, "exactly 300" },
            { NotificationPlan.Firing, "s" + new string('y', 300), "Orders errors", 0.25, "301" },
            { NotificationPlan.Firing, "  " + new string('z', 305) + "  ", new string('n', 200), 0.25, new string('r', 301) },
            // A subject is telemetry: it must not become a link in Teams.
            { NotificationPlan.Firing, "service:[click](http://evil)", "Orders [errors]", 0.25, "[a](b)" },
            { NotificationPlan.Escalated, "service:[click](http://evil)", "Orders errors", 0.25, "x" },
        };

        private static string DeclaredTemplate(string type, string channel)
            => ObservabilityTypes.All().Single(t => t.Key == type).Templates!.Value.GetProperty(channel).GetString()!;

        private static (string Payload, Guid Id, (string Policy, int Step)? Escalation) Legacy(string @event, string subject, string ruleName, double? value, string reason)
        {
            var (rule, alert, transition) = Alert(@event == NotificationPlan.Resolved ? AlertState.Resolved : AlertState.Firing, subject, ruleName, value, reason,
                new() { ["slo"] = "orders-availability", ["sloName"] = "Orders <available> & \"fast\"" });
            var id = Guid.CreateVersion7();
            (string, int)? escalation = @event == NotificationPlan.Escalated ? ("paging", 2) : null;
            return (NotificationPlan.Payload(id, @event, LocalStack.TenantKey, rule, alert, transition, escalation), id, escalation);
        }

        private static async Task<string> RenderAsync(string type, string channel, string payload, Guid deliveryId, (string Policy, int Step)? escalation, IReadOnlyDictionary<string, string>? links = null)
        {
            await TemplateWarmUp.EnsureAsync();
            var node = System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject();
            var legacyType = node["type"]!.GetValue<string>();
            node.Remove("id");
            // The adapter sends the firing/resolved event as data; an escalation's data is the firing transition's.
            if (escalation is not null) node["type"] = NotificationPlan.Firing;
            var model = new RenderModel(type, 1, "Alert", NotificationPriority.High, JsonSerializer.SerializeToElement(node), new Dictionary<string, string>(), links ?? new Dictionary<string, string>(),
                Delivery: new RenderDelivery(deliveryId), Escalation: escalation is { } e ? new RenderEscalation(e.Step, e.Policy) : null);
            _ = legacyType;
            return await TemplateRenderer.RenderAsync(DeclaredTemplate(type, channel), model, Ct);
        }

        [Theory]
        [MemberData(nameof(Variants))]
        public async Task NewModeBody_Webhook_EqualsNotificationPlanPayload_Bytes(string @event, string subject, string ruleName, double? value, string reason)
        {
            var (payload, id, escalation) = Legacy(@event, subject, ruleName, value, reason);
            var type = @event == NotificationPlan.Resolved ? ObservabilityTypes.AlertResolved : ObservabilityTypes.AlertFiring;

            var body = await RenderAsync(type, Channels.Webhook, payload, id, escalation);

            // The delivery's id is the one the legacy payload carries: the whole body is legacy's, byte for byte.
            Assert.Equal(payload, body);
        }

        private const string ConsoleUrl = "https://console.example/";

        private static string LinkOf(string payload)
            => $"{ConsoleUrl}alerts/{JsonDocument.Parse(payload).RootElement.GetProperty("alert").GetProperty("id").GetString()}";

        /// <summary>
        /// Content parity: the same title and text, and the alert's link (legacy's, from the ConsoleUrl's address) as the content's url. The
        /// provider presents the link as its own (Slack's button for legacy's inline link, a card action), so only the content is compared.
        /// </summary>
        [Theory]
        [MemberData(nameof(Variants))]
        public async Task NewModeBody_Slack_Teams_EqualChannelFormatter_Bytes(string @event, string subject, string ruleName, double? value, string reason)
        {
            var (payload, id, escalation) = Legacy(@event, subject, ruleName, value, reason);
            var type = @event == NotificationPlan.Resolved ? ObservabilityTypes.AlertResolved : ObservabilityTypes.AlertFiring;
            var view = NotificationView.Parse(payload);
            var link = LinkOf(payload);
            var links = new Dictionary<string, string> { ["self"] = link };

            var slack = JsonSerializer.Deserialize<ChatContent>(ChannelContent.Chat(await RenderAsync(type, "slack", payload, id, escalation, links), "x", links, null))!;
            var teams = JsonSerializer.Deserialize<ChatContent>(ChannelContent.Chat(await RenderAsync(type, "teams", payload, id, escalation, links), "x", links, null))!;

            var blocks = JsonDocument.Parse(ChannelFormatter.SlackBody(view, ConsoleUrl)).RootElement.GetProperty("blocks");
            var legacyLine = blocks[1].GetProperty("text").GetProperty("text").GetString()!;
            Assert.Equal(blocks[0].GetProperty("text").GetProperty("text").GetString(), slack.Title);
            Assert.Contains($"<{link}|", legacyLine);
            Assert.Equal(System.Text.RegularExpressions.Regex.Replace(legacyLine, @"<https?://[^|>]*\|([^>]*)>", "$1"),
                slack.Text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"));
            Assert.Equal(link, slack.Url);

            var card = JsonDocument.Parse(ChannelFormatter.TeamsBody(view, ConsoleUrl)).RootElement.GetProperty("attachments")[0].GetProperty("content");
            Assert.Equal(card.GetProperty("body")[0].GetProperty("text").GetString(), teams.Title);
            Assert.Equal(card.GetProperty("body")[1].GetProperty("text").GetString(), teams.Text);
            Assert.Equal(link, card.GetProperty("actions")[0].GetProperty("url").GetString());
            Assert.Equal(link, teams.Url);
            // A subject can't write a link of its own into the card.
            Assert.DoesNotContain("](", teams.Text);
        }

        [Theory]
        [MemberData(nameof(Variants))]
        public async Task NewModeBody_Email_EqualsChannelFormatter_Bytes(string @event, string subject, string ruleName, double? value, string reason)
        {
            var (payload, id, escalation) = Legacy(@event, subject, ruleName, value, reason);
            var type = @event == NotificationPlan.Resolved ? ObservabilityTypes.AlertResolved : ObservabilityTypes.AlertFiring;
            var link = LinkOf(payload);

            var (legacySubject, legacyBody) = ChannelFormatter.Email(NotificationView.Parse(payload), ConsoleUrl);
            var email = JsonSerializer.Deserialize<EmailContent>(ChannelContent.Email(
                await RenderAsync(type, Channels.Email, payload, id, escalation, new Dictionary<string, string> { ["self"] = link }), "x"))!;

            Assert.Equal(legacySubject, email.Subject);
            Assert.Contains(link, legacyBody);
            Assert.Equal(legacyBody.TrimEnd().ReplaceLineEndings("\n"), email.Text.TrimEnd().ReplaceLineEndings("\n"));
        }

        /// <summary>The shadow comparer reads a ported type's chat and email deliveries as legacy's: no difference, with the ConsoleUrl's address set.</summary>
        [Theory]
        [MemberData(nameof(Variants))]
        public async Task ShadowCompare_PortedType_ChatAndEmail_NoUnexplainedDifference(string @event, string subject, string ruleName, double? value, string reason)
        {
            var (payload, id, escalation) = Legacy(@event, subject, ruleName, value, reason);
            var type = @event == NotificationPlan.Resolved ? ObservabilityTypes.AlertResolved : ObservabilityTypes.AlertFiring;
            var links = new Dictionary<string, string> { ["self"] = LinkOf(payload) };

            var slack = ChannelContent.Chat(await RenderAsync(type, "slack", payload, id, escalation, links), "x", links, null);
            var teams = ChannelContent.Chat(await RenderAsync(type, "teams", payload, id, escalation, links), "x", links, null);
            var email = ChannelContent.Email(await RenderAsync(type, Channels.Email, payload, id, escalation, links), "x");

            Assert.Null(Juice.Observability.Notifications.Shadow.ShadowBodies.Formatted(ChannelKind.Slack, payload, slack, ConsoleUrl));
            Assert.Null(Juice.Observability.Notifications.Shadow.ShadowBodies.Formatted(ChannelKind.Teams, payload, teams, ConsoleUrl));
            Assert.Null(Juice.Observability.Notifications.Shadow.ShadowBodies.Formatted(ChannelKind.Email, payload, email, ConsoleUrl));
        }

        // ─── Live ─────────────────────────────────────────────────────────────

        private const string LiveSecret = "JUICE_WEBHOOK_M46C";

        /// <summary>One firing sent through <paramref name="pipeline"/> to a stub receiver: the body it got, and the delivery's template version in new.</summary>
        private static async Task<string> CaptureAsync(string pipeline, int templateVersion)
        {
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var receiver = await WebhookTests.Receiver.StartAsync();
            Environment.SetEnvironmentVariable(LiveSecret, "m46c-signing-secret");
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string>
            {
                ["Notifications:Pipeline"] = pipeline,
                ["Webhooks:Enabled"] = "true", ["Webhooks:AllowHttpToAllowlisted"] = "true", ["Webhooks:Interval"] = "00:00:00.200", ["Webhooks:MaxAttempts"] = "1",
                ["Notifications:Delivery:AllowHttpToAllowlisted"] = "true", ["Notifications:Delivery:MaxAttempts"] = "1",
                ["Notifications:Delivery:SmtpServers:observability:Host"] = "smtp.example.com",
                ["Notifications:Stages:Planner:Poll"] = "00:00:00.200", ["Notifications:Stages:DeliveryPlanner:Poll"] = "00:00:00.200", ["Notifications:Stages:Dispatcher:Poll"] = "00:00:00.200",
            });
            using var client = factory.CreateClient();
            var (_, _, admin, tenant) = await Caller.AllAsync(client, database);
            var webhooks = $"/api/v1/tenants/{tenant}/webhooks";
            await Caller.JsonAsync(await admin.SendAsync(HttpMethod.Put, $"{webhooks}/allowlist", new { destination = $"{receiver.Address}/32", ports = new[] { receiver.Port } }));
            await Caller.JsonAsync(await admin.PostAsync(webhooks, new { key = "ops", url = receiver.Url, secretRef = $"env:{LiveSecret}", events = new[] { "alert.firing" } }));

            var (_, transition) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(factory, database, transition);
            for (var i = 0; i < 150 && receiver.Requests.IsEmpty; i++)
            {
                await Task.Delay(200, Ct);
            }
            Assert.False(receiver.Requests.IsEmpty, $"nothing reached the receiver through {pipeline}");
            if (pipeline == "new")
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                Assert.Equal(templateVersion, await notifications.Deliveries.AsNoTracking().Select(d => d.TemplateVersion).MaxAsync(Ct));
            }
            return receiver.Requests.First().Body;
        }

        private static string Normalised(string body)
        {
            body = Regex.Replace(body, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", "GUID");
            return Regex.Replace(body, @"\d{4}-\d\d-\d\dT[\d:.]+(?:Z|[+-]\d\d:\d\d)", "T");
        }

        /// <summary>
        /// The same transition sent in legacy and in new reaches a receiver as the same bytes, but for the ids and the times (the receiver's
        /// signature headers aren't part of the body). The webhook body is the signed one; the chat and email bodies are the formatter's text
        /// through the provider (checked per channel, without a receiver, by the NewModeBody tests above).
        /// </summary>
        [Fact]
        public async Task LiveNew_ReceiverBytes_EqualLegacy_Webhook_Chat_Email()
        {
            LocalStack.RequireAvailable();
            var legacy = await CaptureAsync("legacy", 0);
            var @new = await CaptureAsync("new", 1);

            Assert.Equal(Normalised(legacy), Normalised(@new));
        }

        // ─── The declaration ──────────────────────────────────────────────────

        [Fact]
        public void ObservabilityTypes_DeclareTemplates_Version1()
        {
            var types = ObservabilityTypes.All().ToList();
            foreach (var key in new[] { ObservabilityTypes.AlertFiring, ObservabilityTypes.AlertResolved })
            {
                var templates = types.Single(t => t.Key == key).Templates;
                Assert.NotNull(templates);
                Assert.Equal(["webhook", "slack", "teams", "email"], templates!.Value.EnumerateObject().Select(p => p.Name));
            }
            // The action request types keep the pipeline's defaults.
            Assert.All(types.Where(t => t.Key!.Contains(".action.")), t => Assert.Null(t.Templates));
        }

        /// <summary>
        /// A host declares the alert types at start: a restart finds the same declaration (no third version), and a host that started with the old
        /// declaration (another hash, no templates) is given the next version by this one, with a template per channel (upgrade).
        /// </summary>
        [Fact]
        public async Task ObservabilityTypes_Restart_NoThirdVersion_OldDeclaration_GetsNewVersion()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var settings = FileSettings(LocalStack.NotificationsPipeline ?? "legacy");
            static async Task<int[]> VersionsAsync(ApiTests.ApiFactory factory)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                return [.. await notifications.Types.AsNoTracking().Where(v => v.Key == ObservabilityTypes.AlertFiring).OrderBy(v => v.Version).Select(v => v.Version).ToListAsync(Ct)];
            }
            await using (var first = new ApiTests.ApiFactory(database, settings: settings))
            {
                _ = first.CreateClient();
                Assert.Equal(new[] { 1 }, await VersionsAsync(first));
            }
            await using (var restart = new ApiTests.ApiFactory(database, settings: settings))
            {
                _ = restart.CreateClient();
                Assert.Equal(new[] { 1 }, await VersionsAsync(restart));
                // As if version 1 were declared by the host before the templates were ported.
                await using var scope = restart.Services.CreateAsyncScope();
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
                var version1 = await notifications.Types.Where(t => t.Key == ObservabilityTypes.AlertFiring).Select(t => t.Id).SingleAsync(Ct);
                await notifications.Templates.Where(t => t.TypeId == version1).ExecuteDeleteAsync(Ct);
                await notifications.Types.Where(t => t.Id == version1).ExecuteUpdateAsync(s => s.SetProperty(t => t.DeclarationHash, "declared-before-the-port"), Ct);
            }

            await using var upgraded = new ApiTests.ApiFactory(database, settings: settings);
            _ = upgraded.CreateClient();

            Assert.Equal(new[] { 1, 2 }, await VersionsAsync(upgraded));
            await using var check = upgraded.Services.CreateAsyncScope();
            var db = check.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            var latest = await db.Types.AsNoTracking().SingleAsync(t => t.Key == ObservabilityTypes.AlertFiring && t.Version == 2, Ct);
            Assert.Equal(["email", "slack", "teams", "webhook"], await db.Templates.AsNoTracking().Where(t => t.TypeId == latest.Id).Select(t => t.Channel).OrderBy(c => c).ToListAsync(Ct));

            // And a restart after the bump declares nothing again: still two versions, the latest the same.
            await using var restarted = new ApiTests.ApiFactory(database, settings: settings);
            _ = restarted.CreateClient();
            Assert.Equal(new[] { 1, 2 }, await VersionsAsync(restarted));
            await using var again = restarted.Services.CreateAsyncScope();
            Assert.Equal(latest.DeclarationHash, (await again.ServiceProvider.GetRequiredService<NotificationsDbContext>().Types.AsNoTracking()
                .SingleAsync(t => t.Key == ObservabilityTypes.AlertFiring && t.Version == 2, Ct)).DeclarationHash);
        }
    }
}
