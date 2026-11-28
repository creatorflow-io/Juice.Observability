using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Tests.Integrations.Sample;
using Juice.Observability.Tests.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Juice.Observability.Tests.Integrations.Instances
{
    /// <summary>The instance API on the local stack: the lifecycle, tenancy, concurrency and the secret canary (AC-47).</summary>
    [Trait("Milestone", "INT-I1")]
    public sealed class InstanceApiTests
    {
        public const string Canary = "canary-7c1e9a40-do-not-leak";
        public const string TokenVariable = "JUICE_INTEGRATION_SAMPLE_TOKEN";

        static InstanceApiTests() => Environment.SetEnvironmentVariable(TokenVariable, Canary);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private sealed class Rig : IAsyncDisposable
        {
            public required ScratchDatabase Database { get; init; }
            public required ModuleTestHost Host { get; init; }
            public required LogCapture Logs { get; init; }
            public SampleJobsControl Control => Host.App.Services.GetRequiredService<SampleJobsControl>();
            public HttpClient Admin { get; set; } = null!;
            public string Base => $"/api/v1/tenants/{Host.TenantId}/integrations";
            public List<string> Responses { get; } = [];

            public async ValueTask DisposeAsync()
            {
                await Host.DisposeAsync();
                await Database.DisposeAsync();
            }

            public async Task<(HttpResponseMessage Response, JsonElement Json)> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, string? ifMatch = null, string? key = null)
            {
                using var request = new HttpRequestMessage(method, path);
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body);
                }
                if (ifMatch is not null)
                {
                    request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
                }
                if (key is not null)
                {
                    request.Headers.Add("Idempotency-Key", key);
                }
                var response = await client.SendAsync(request, Ct);
                var text = await response.Content.ReadAsStringAsync(Ct);
                Responses.Add(text);
                return (response, text.Length > 0 && text[0] is '{' or '[' ? JsonDocument.Parse(text).RootElement.Clone() : default);
            }

            public Task<(HttpResponseMessage Response, JsonElement Json)> Post(string path, object? body = null, string? key = null) => SendAsync(Admin, HttpMethod.Post, Base + path, body, key: key);

            public async Task<JsonElement> CreateAsync(string name = "jobs-a", Action<Dictionary<string, object>>? change = null)
            {
                var (response, json) = await Post("/instances", Body(name, change));
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                return json;
            }

            public async Task<string> EtagAsync(Guid id)
            {
                using var response = await Admin.GetAsync($"{Base}/instances/{id}", Ct);
                return response.Headers.ETag!.Tag;
            }

            public async Task<long> CountAsync(string sql)
            {
                await using var connection = new NpgsqlConnection(Database.ConnectionString);
                await connection.OpenAsync(Ct);
                await using var command = new NpgsqlCommand(sql, connection);
                return Convert.ToInt64(await command.ExecuteScalarAsync(Ct));
            }

            public async Task<string?> TextAsync(string sql)
            {
                await using var connection = new NpgsqlConnection(Database.ConnectionString);
                await connection.OpenAsync(Ct);
                await using var command = new NpgsqlCommand(sql, connection);
                return (await command.ExecuteScalarAsync(Ct)) as string;
            }

            public async Task ExecuteAsync(string sql)
            {
                await using var connection = new NpgsqlConnection(Database.ConnectionString);
                await connection.OpenAsync(Ct);
                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync(Ct);
            }
        }

        private static Dictionary<string, object> Body(string name, Action<Dictionary<string, object>>? change = null)
        {
            var body = new Dictionary<string, object>
            {
                ["pluginKey"] = "sample.jobs", ["name"] = name,
                ["config"] = new Dictionary<string, object> { ["baseUrl"] = "https://jobs.example.com", ["batchSize"] = 10 },
                ["scope"] = new Dictionary<string, object> { ["projects"] = new[] { "alpha" } },
                ["secretRefs"] = new Dictionary<string, object> { ["token"] = "env:" + TokenVariable },
                ["intervalSeconds"] = 300,
            };
            change?.Invoke(body);
            return body;
        }

        private static async Task<Rig> StartAsync(IReadOnlyDictionary<string, string?>? settings = null)
        {
            LocalStack.RequireAvailable();
            var database = await LocalStack.CreateDatabaseAsync();
            var logs = new LogCapture();
            try
            {
                var host = await ModuleTestHost.StartAsync(b =>
                {
                    b.AddObservabilityModule<SampleModule>();
                    b.Services.AddSingleton<ILoggerProvider>(logs);
                }, database, hostedServices: true, settings: settings);
                return new Rig { Database = database, Host = host, Logs = logs, Admin = host.As("admin") };
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        private static Guid IdOf(JsonElement json) => json.GetProperty("id").GetGuid();

        [Fact]
        public async Task Create_validate_enable_disable_delete_audited()
        {
            await using var rig = await StartAsync();
            var created = await rig.CreateAsync();
            var id = IdOf(created);
            Assert.Equal("Configured", created.GetProperty("state").GetString());
            Assert.Equal("Sample jobs", created.GetProperty("pluginName").GetString());

            var (validated, body) = await rig.Post($"/instances/{id}/validate");
            Assert.Equal(HttpStatusCode.OK, validated.StatusCode);
            Assert.True(body.GetProperty("report").GetProperty("passed").GetBoolean());
            Assert.Equal("Validated", body.GetProperty("instance").GetProperty("state").GetString());

            var (enabled, enabledBody) = await rig.Post($"/instances/{id}/enable");
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            Assert.Equal("Enabled", enabledBody.GetProperty("state").GetString());
            Assert.NotEqual(JsonValueKind.Null, enabledBody.GetProperty("nextRunAt").ValueKind);

            var (disabled, disabledBody) = await rig.Post($"/instances/{id}/disable");
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            Assert.Equal("Disabled", disabledBody.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, disabledBody.GetProperty("nextRunAt").ValueKind);
            // disable is idempotent
            Assert.Equal(HttpStatusCode.OK, (await rig.Post($"/instances/{id}/disable")).Response.StatusCode);

            var (deleted, _) = await rig.SendAsync(rig.Admin, HttpMethod.Delete, $"{rig.Base}/instances/{id}", ifMatch: await rig.EtagAsync(id));
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.Equal(0, await rig.CountAsync("SELECT count(*) FROM integrations.integration_instances"));

            foreach (var operation in new[] { "create", "validate", "enable", "disable", "delete" })
            {
                Assert.Equal(1, await rig.CountAsync($"SELECT count(*) FROM audit_records WHERE operation = 'integrations.instance.{operation}' AND outcome = 'Succeeded'"));
            }
            // plugin, state and hash only: never the configuration or the references
            Assert.Equal(0, await rig.CountAsync("SELECT count(*) FROM audit_records WHERE operation LIKE 'integrations.%' AND (metadata::text LIKE '%baseUrl%' OR metadata::text LIKE '%env:%')"));
            Assert.Equal(1, await rig.CountAsync("SELECT count(*) FROM audit_records WHERE operation = 'integrations.instance.enable' AND metadata::text LIKE '%sample.jobs%' AND metadata::text LIKE '%Enabled%' AND metadata::text LIKE '%hash%'"));
        }

        [Fact]
        public async Task Viewer_and_operator_read_state_without_config_or_refs()
        {
            await using var rig = await StartAsync();
            var id = IdOf(await rig.CreateAsync());
            foreach (var role in new[] { "viewer", "operator" })
            {
                using var client = rig.Host.As(role);
                var (_, one) = await rig.SendAsync(client, HttpMethod.Get, $"{rig.Base}/instances/{id}");
                Assert.Equal("Configured", one.GetProperty("state").GetString());
                Assert.Equal(JsonValueKind.Null, one.GetProperty("config").ValueKind);
                Assert.Equal(JsonValueKind.Null, one.GetProperty("scope").ValueKind);
                Assert.Equal(JsonValueKind.Null, one.GetProperty("secretRefs").ValueKind);
                Assert.Equal(JsonValueKind.Null, one.GetProperty("lastValidation").ValueKind);
                var (_, plugins) = await rig.SendAsync(client, HttpMethod.Get, $"{rig.Base}/plugins");
                Assert.Equal("sample.jobs", plugins[0].GetProperty("key").GetString());
            }
            var (_, list) = await rig.SendAsync(rig.Admin, HttpMethod.Get, $"{rig.Base}/instances");
            var item = list.GetProperty("items")[0];
            Assert.Equal(JsonValueKind.Null, item.GetProperty("config").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("secretRefs").ValueKind);
            var (_, mine) = await rig.SendAsync(rig.Admin, HttpMethod.Get, $"{rig.Base}/instances/{id}");
            Assert.Equal("env:" + TokenVariable, mine.GetProperty("secretRefs").GetProperty("token").GetString());
            Assert.Equal(10, mine.GetProperty("config").GetProperty("batchSize").GetInt32());
            // filters
            var (_, none) = await rig.SendAsync(rig.Admin, HttpMethod.Get, $"{rig.Base}/instances?state=Enabled");
            Assert.Empty(none.GetProperty("items").EnumerateArray());
            Assert.Equal(HttpStatusCode.BadRequest, (await rig.SendAsync(rig.Admin, HttpMethod.Get, $"{rig.Base}/instances?state=Bogus")).Response.StatusCode);
        }

        [Fact]
        public async Task Non_admin_mutation_403_audited_denied()
        {
            await using var rig = await StartAsync();
            using var op = rig.Host.As("operator");
            var (response, _) = await rig.SendAsync(op, HttpMethod.Post, rig.Base + "/instances", Body("jobs-a"));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, await rig.CountAsync("SELECT count(*) FROM integrations.integration_instances"));
            Assert.Equal(1, await rig.CountAsync("SELECT count(*) FROM audit_records WHERE operation = 'integrations.instance.create' AND outcome = 'Denied'"));

            var id = IdOf(await rig.CreateAsync());
            foreach (var action in new[] { "validate", "enable", "disable" })
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await rig.SendAsync(op, HttpMethod.Post, $"{rig.Base}/instances/{id}/{action}")).Response.StatusCode);
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await rig.SendAsync(op, HttpMethod.Delete, $"{rig.Base}/instances/{id}", ifMatch: "\"1\"")).Response.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await rig.SendAsync(op, HttpMethod.Put, $"{rig.Base}/instances/{id}", Body("x"), ifMatch: "\"1\"")).Response.StatusCode);
            Assert.Equal(6, await rig.CountAsync("SELECT count(*) FROM audit_records WHERE operation LIKE 'integrations.instance.%' AND outcome = 'Denied'"));
        }

        private static async Task<(Guid Id, string Key)> AddTenantAsync(Rig rig)
        {
            await using var db = rig.Database.Context();
            var tenant = new PlatformTenant { Key = Guid.CreateVersion7().ToString(), DisplayName = "other" };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(Ct);
            return (tenant.Id, tenant.Key);
        }

        [Fact]
        public async Task Other_tenant_route_403_other_tenant_id_404()
        {
            await using var rig = await StartAsync();
            var id = IdOf(await rig.CreateAsync());
            var (otherId, otherKey) = await AddTenantAsync(rig);
            // the deployment's admin on another tenant's route
            Assert.Equal(HttpStatusCode.Forbidden, (await rig.SendAsync(rig.Admin, HttpMethod.Get, $"/api/v1/tenants/{otherId}/integrations/instances")).Response.StatusCode);
            // the other tenant's own caller, asking for the deployment tenant's instance by its id
            using var other = rig.Host.As("admin", otherKey);
            Assert.Equal(HttpStatusCode.NotFound, (await rig.SendAsync(other, HttpMethod.Get, $"/api/v1/tenants/{otherId}/integrations/instances/{id}")).Response.StatusCode);
            var (_, list) = await rig.SendAsync(other, HttpMethod.Get, $"/api/v1/tenants/{otherId}/integrations/instances");
            Assert.Empty(list.GetProperty("items").EnumerateArray());
            // and the deployment tenant's caller can't reach it through the other tenant's route either
            Assert.Equal(HttpStatusCode.Forbidden, (await rig.SendAsync(rig.Admin, HttpMethod.Get, $"/api/v1/tenants/{otherId}/integrations/instances/{id}")).Response.StatusCode);
        }

        [Fact]
        public async Task Mutation_in_non_deployment_tenant_403()
        {
            await using var rig = await StartAsync();
            var (otherId, otherKey) = await AddTenantAsync(rig);
            using var other = rig.Host.As("admin", otherKey);
            var (response, _) = await rig.SendAsync(other, HttpMethod.Post, $"/api/v1/tenants/{otherId}/integrations/instances", Body("jobs-a"));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, await rig.CountAsync("SELECT count(*) FROM integrations.integration_instances"));
            Assert.Equal(1, await rig.CountAsync($"SELECT count(*) FROM audit_records WHERE operation = 'integrations.instance.create' AND outcome = 'Denied' AND tenant_id = '{otherId}'"));
        }

        [Fact]
        public async Task Create_validation_errors_are_keyed_by_field()
        {
            await using var rig = await StartAsync();
            var (response, json) = await rig.Post("/instances", Body("", b =>
            {
                b["config"] = new Dictionary<string, object> { ["batchSize"] = 5000, ["surprise"] = true };
                b["scope"] = new Dictionary<string, object> { ["projects"] = "alpha" };
                b["secretRefs"] = new Dictionary<string, object> { ["token"] = "env:PATH", ["extra"] = "env:JUICE_INTEGRATION_X" };
                b["intervalSeconds"] = 5;
            }));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = json.GetProperty("errors");
            foreach (var key in new[] { "name", "config.baseUrl", "config.batchSize", "config.surprise", "scope.projects", "secretRefs.token", "secretRefs.extra", "intervalSeconds" })
            {
                Assert.True(errors.TryGetProperty(key, out _), key);
            }
            Assert.Equal(0, await rig.CountAsync("SELECT count(*) FROM integrations.integration_instances"));
            // a plugin that isn't installed
            Assert.Equal(HttpStatusCode.Conflict, (await rig.Post("/instances", Body("x", b => b["pluginKey"] = "sample.nothing"))).Response.StatusCode);
        }

        [Fact]
        public async Task Same_idempotency_key_one_row_same_response()
        {
            await using var rig = await StartAsync();
            var (first, one) = await rig.Post("/instances", Body("jobs-a"), key: "k-1");
            var (second, two) = await rig.Post("/instances", Body("jobs-a"), key: "k-1");
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
            Assert.True(second.Headers.Contains("Idempotency-Replayed"));
            Assert.Equal(IdOf(one), IdOf(two));
            Assert.Equal(1, await rig.CountAsync("SELECT count(*) FROM integrations.integration_instances"));
            // the same key with another request is refused
            Assert.Equal(HttpStatusCode.UnprocessableEntity, (await rig.Post("/instances", Body("jobs-b"), key: "k-1")).Response.StatusCode);
        }

        [Fact]
        public async Task Concurrent_create_same_name_one_201_one_409()
        {
            await using var rig = await StartAsync();
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => rig.Post("/instances", Body(i == 0 ? "Jobs-A" : "jobs-a"))));
            var codes = results.Select(r => r.Response.StatusCode).Order().ToList();
            Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], codes);
            Assert.Equal(1, await rig.CountAsync("SELECT count(*) FROM integrations.integration_instances"));
        }

        [Fact]
        public async Task Put_stale_if_match_412_missing_428()
        {
            await using var rig = await StartAsync();
            var created = await rig.CreateAsync();
            var id = IdOf(created);
            var etag = created.GetProperty("etag").GetString()!;
            var url = $"{rig.Base}/instances/{id}";
            Assert.Equal((HttpStatusCode)428, (await rig.SendAsync(rig.Admin, HttpMethod.Put, url, Body("renamed"))).Response.StatusCode);
            Assert.Equal((HttpStatusCode)428, (await rig.SendAsync(rig.Admin, HttpMethod.Delete, url)).Response.StatusCode);
            var (ok, renamed) = await rig.SendAsync(rig.Admin, HttpMethod.Put, url, Body("renamed"), ifMatch: etag);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal("renamed", renamed.GetProperty("name").GetString());
            // the old etag is stale now
            Assert.Equal(HttpStatusCode.PreconditionFailed, (await rig.SendAsync(rig.Admin, HttpMethod.Put, url, Body("again"), ifMatch: etag)).Response.StatusCode);
            Assert.Equal(HttpStatusCode.PreconditionFailed, (await rig.SendAsync(rig.Admin, HttpMethod.Delete, url, ifMatch: etag)).Response.StatusCode);
            Assert.Equal(1, await rig.CountAsync("SELECT count(*) FROM integrations.integration_instances WHERE name = 'renamed'"));
        }

        [Fact]
        public async Task Config_change_resets_to_configured()
        {
            await using var rig = await StartAsync();
            var id = IdOf(await rig.CreateAsync());
            await rig.Post($"/instances/{id}/validate");
            var (_, enabled) = await rig.Post($"/instances/{id}/enable");
            Assert.Equal("Enabled", enabled.GetProperty("state").GetString());
            Assert.NotNull(await rig.TextAsync($"SELECT validated_hash FROM integrations.integration_instances WHERE id = '{id}'"));

            var (response, changed) = await rig.SendAsync(rig.Admin, HttpMethod.Put, $"{rig.Base}/instances/{id}",
                Body("jobs-a", b => b["config"] = new Dictionary<string, object> { ["baseUrl"] = "https://other.example.com" }), ifMatch: enabled.GetProperty("etag").GetString());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Configured", changed.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, changed.GetProperty("nextRunAt").ValueKind);
            Assert.Null(await rig.TextAsync($"SELECT validated_hash FROM integrations.integration_instances WHERE id = '{id}'"));
            Assert.Equal(HttpStatusCode.Conflict, (await rig.Post($"/instances/{id}/enable")).Response.StatusCode);
        }

        [Fact]
        public async Task Validate_racing_update_409_nothing_saved()
        {
            await using var rig = await StartAsync();
            var created = await rig.CreateAsync();
            var id = IdOf(created);
            // while the plugin validates, another admin changes the configuration
            rig.Control.OnValidate = async () =>
            {
                var (put, _) = await rig.SendAsync(rig.Host.As("admin"), HttpMethod.Put, $"{rig.Base}/instances/{id}",
                    Body("jobs-a", b => b["config"] = new Dictionary<string, object> { ["baseUrl"] = "https://changed.example.com" }), ifMatch: created.GetProperty("etag").GetString());
                Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            };
            var (response, _) = await rig.Post($"/instances/{id}/validate");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("Configured", await rig.TextAsync($"SELECT state FROM integrations.integration_instances WHERE id = '{id}'"));
            Assert.Null(await rig.TextAsync($"SELECT validated_hash FROM integrations.integration_instances WHERE id = '{id}'"));
            Assert.Null(await rig.TextAsync($"SELECT last_validation::text FROM integrations.integration_instances WHERE id = '{id}'"));
            Assert.Equal(0, await rig.CountAsync("SELECT count(*) FROM audit_records WHERE operation = 'integrations.instance.validate'"));
        }

        [Fact]
        public async Task Validate_timeout_stays_configured_with_report()
        {
            await using var rig = await StartAsync(new Dictionary<string, string?> { ["Modules:integrations:ValidateTimeout"] = "00:00:01" });
            var id = IdOf(await rig.CreateAsync());
            rig.Control.ValidateDelay = TimeSpan.FromSeconds(30);
            var (response, body) = await rig.Post($"/instances/{id}/validate");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(body.GetProperty("report").GetProperty("passed").GetBoolean());
            Assert.Equal("timeout", body.GetProperty("report").GetProperty("checks")[0].GetProperty("name").GetString());
            Assert.Equal("Configured", body.GetProperty("instance").GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Object, body.GetProperty("instance").GetProperty("lastValidation").ValueKind);
        }

        [Fact]
        public async Task Validate_plugin_throws_stays_configured()
        {
            await using var rig = await StartAsync();
            var id = IdOf(await rig.CreateAsync());
            rig.Control.ThrowOnValidate = true;
            var (response, body) = await rig.Post($"/instances/{id}/validate");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(body.GetProperty("report").GetProperty("passed").GetBoolean());
            Assert.Equal("Configured", body.GetProperty("instance").GetProperty("state").GetString());
            Assert.Equal(0, await rig.CountAsync($"SELECT count(*) FROM integrations.integration_instances WHERE id = '{id}' AND validated_hash IS NOT NULL"));
            // a failed validation of a validated instance takes it back to Configured
            rig.Control.ThrowOnValidate = false;
            await rig.Post($"/instances/{id}/validate");
            rig.Control.ValidatePasses = false;
            var (_, again) = await rig.Post($"/instances/{id}/validate");
            Assert.Equal("Configured", again.GetProperty("instance").GetProperty("state").GetString());
        }

        [Fact]
        public async Task Missing_secret_reference_target_fails_validation_naming_the_secret_only()
        {
            await using var rig = await StartAsync();
            var id = IdOf(await rig.CreateAsync("jobs-b", b => b["secretRefs"] = new Dictionary<string, object> { ["token"] = "env:JUICE_INTEGRATION_NOT_SET_ANYWHERE" }));
            var (_, body) = await rig.Post($"/instances/{id}/validate");
            Assert.False(body.GetProperty("report").GetProperty("passed").GetBoolean());
            var check = body.GetProperty("report").GetProperty("checks")[0];
            Assert.Equal("secrets", check.GetProperty("name").GetString());
            Assert.Contains("token", check.GetProperty("detail").GetString());
        }

        [Fact]
        public async Task Enable_needs_current_validated_hash()
        {
            await using var rig = await StartAsync();
            var id = IdOf(await rig.CreateAsync());
            Assert.Equal(HttpStatusCode.Conflict, (await rig.Post($"/instances/{id}/enable")).Response.StatusCode);   // never validated
            await rig.Post($"/instances/{id}/validate");
            // the stored configuration is changed behind the API's back: what was validated is not what is there
            await rig.ExecuteAsync($"UPDATE integrations.integration_instances SET config = '{{\"baseUrl\":\"https://sneaky.example.com\"}}' WHERE id = '{id}'");
            Assert.Equal(HttpStatusCode.Conflict, (await rig.Post($"/instances/{id}/enable")).Response.StatusCode);
            // validated again, enabled, disabled: enable from Disabled needs the same hash
            await rig.Post($"/instances/{id}/validate");
            await rig.Post($"/instances/{id}/enable");
            await rig.Post($"/instances/{id}/disable");
            Assert.Equal(HttpStatusCode.OK, (await rig.Post($"/instances/{id}/enable")).Response.StatusCode);
            await rig.Post($"/instances/{id}/disable");
            await rig.ExecuteAsync($"UPDATE integrations.integration_instances SET secret_refs = '{{\"token\":\"env:JUICE_INTEGRATION_OTHER\"}}' WHERE id = '{id}'");
            Assert.Equal(HttpStatusCode.Conflict, (await rig.Post($"/instances/{id}/enable")).Response.StatusCode);
        }

        [Fact]
        public async Task Enable_with_plugin_not_installed_409()
        {
            await using var rig = await StartAsync();
            var id = IdOf(await rig.CreateAsync());
            await rig.Post($"/instances/{id}/validate");
            await rig.ExecuteAsync($"UPDATE integrations.integration_instances SET plugin_key = 'sample.gone' WHERE id = '{id}'");
            var (response, _) = await rig.Post($"/instances/{id}/enable");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await rig.Post($"/instances/{id}/validate")).Response.StatusCode);
            var (_, one) = await rig.SendAsync(rig.Admin, HttpMethod.Get, $"{rig.Base}/instances/{id}");
            Assert.Equal(JsonValueKind.Null, one.GetProperty("pluginName").ValueKind);
            Assert.Equal("failing", one.GetProperty("health").GetProperty("runtime").GetString());
        }

        [Fact]
        public async Task Delete_running_409_delete_disabled_cascades_checkpoints()
        {
            await using var rig = await StartAsync();
            var id = IdOf(await rig.CreateAsync());
            await rig.Post($"/instances/{id}/validate");
            await rig.Post($"/instances/{id}/enable");
            Assert.Equal(HttpStatusCode.Conflict, (await rig.SendAsync(rig.Admin, HttpMethod.Delete, $"{rig.Base}/instances/{id}", ifMatch: await rig.EtagAsync(id))).Response.StatusCode);
            await rig.Post($"/instances/{id}/disable");
            await rig.ExecuteAsync($"INSERT INTO integrations.integration_checkpoints (instance_id, stream, watermark, cursor, updated_at) VALUES ('{id}', 'jobs', now(), 'c', now())");
            Assert.Equal(1, await rig.CountAsync("SELECT count(*) FROM integrations.integration_checkpoints"));
            Assert.Equal(HttpStatusCode.NoContent, (await rig.SendAsync(rig.Admin, HttpMethod.Delete, $"{rig.Base}/instances/{id}", ifMatch: await rig.EtagAsync(id))).Response.StatusCode);
            Assert.Equal(0, await rig.CountAsync("SELECT count(*) FROM integrations.integration_checkpoints"));
            Assert.Equal(HttpStatusCode.NotFound, (await rig.SendAsync(rig.Admin, HttpMethod.Get, $"{rig.Base}/instances/{id}")).Response.StatusCode);
        }

        [Fact]
        public async Task Config_version_mismatch_refuses_enable()
        {
            await using var rig = await StartAsync();
            var id = IdOf(await rig.CreateAsync());
            await rig.Post($"/instances/{id}/validate");
            await rig.ExecuteAsync($"UPDATE integrations.integration_instances SET config_version = 2 WHERE id = '{id}'");
            var (response, _) = await rig.Post($"/instances/{id}/enable");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var (_, one) = await rig.SendAsync(rig.Admin, HttpMethod.Get, $"{rig.Base}/instances/{id}");
            Assert.Equal("Failed", one.GetProperty("state").GetString());
            Assert.Equal("config version 2, plugin expects 1", one.GetProperty("stateReason").GetString());
            // saving the configuration again brings it to the plugin's version
            var (put, fixedUp) = await rig.SendAsync(rig.Admin, HttpMethod.Put, $"{rig.Base}/instances/{id}", Body("jobs-a"), ifMatch: await rig.EtagAsync(id));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            Assert.Equal("Configured", fixedUp.GetProperty("state").GetString());
            Assert.Equal(1, fixedUp.GetProperty("configVersion").GetInt32());
        }

        [Fact]
        public async Task Validate_of_a_plugin_that_ignores_cancellation_still_times_out()
        {
            await using var rig = await StartAsync(new Dictionary<string, string?> { ["Modules:integrations:ValidateTimeout"] = "00:00:01" });
            var id = IdOf(await rig.CreateAsync());
            rig.Control.ValidateDelay = TimeSpan.FromSeconds(25);
            rig.Control.IgnoreCancellation = true;
            var started = DateTime.UtcNow;
            var (response, body) = await rig.Post($"/instances/{id}/validate");
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "the request waited for the plugin");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("timeout", body.GetProperty("report").GetProperty("checks")[0].GetProperty("name").GetString());
        }

        [Theory]
        [InlineData("W/\"{0}\"")]
        [InlineData("\"999\", \"{0}\"")]
        [InlineData("*")]
        public async Task If_match_accepts_weak_tags_lists_and_star(string template)
        {
            await using var rig = await StartAsync();
            var created = await rig.CreateAsync();
            var id = IdOf(created);
            var version = created.GetProperty("etag").GetString()!.Trim('"');
            var (response, _) = await rig.SendAsync(rig.Admin, HttpMethod.Put, $"{rig.Base}/instances/{id}", Body("renamed"), ifMatch: string.Format(template, version));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // a list with no current tag is stale
            Assert.Equal(HttpStatusCode.PreconditionFailed, (await rig.SendAsync(rig.Admin, HttpMethod.Put, $"{rig.Base}/instances/{id}", Body("again"), ifMatch: "\"998\", W/\"999\"")).Response.StatusCode);
        }

        [Fact]
        public async Task Max_instances_holds_under_concurrent_creates()
        {
            await using var rig = await StartAsync(new Dictionary<string, string?> { ["Modules:integrations:MaxInstances"] = "3" });
            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => rig.Post("/instances", Body($"jobs-{i}"))));
            Assert.Equal(3, results.Count(r => r.Response.StatusCode == HttpStatusCode.Created));
            Assert.Equal(7, results.Count(r => r.Response.StatusCode == HttpStatusCode.Conflict));
            Assert.Equal(3, await rig.CountAsync("SELECT count(*) FROM integrations.integration_instances"));
        }

        private async Task<string> EverythingTextAsync(Rig rig)
        {
            // every table of the platform database and of the module, as text: the canary appears in none
            var tables = new List<(string Schema, string Name)>();
            await using var connection = new NpgsqlConnection(rig.Database.ConnectionString);
            await connection.OpenAsync(Ct);
            await using (var list = new NpgsqlCommand("SELECT table_schema, table_name FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema')", connection))
            await using (var reader = await list.ExecuteReaderAsync(Ct))
            {
                while (await reader.ReadAsync(Ct))
                {
                    tables.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
            var text = new StringBuilder();
            foreach (var (schema, name) in tables)
            {
                await using var command = new NpgsqlCommand($"SELECT coalesce(string_agg(t::text, E'\\n'), '') FROM \"{schema}\".\"{name}\" t", connection);
                text.AppendLine((string?)await command.ExecuteScalarAsync(Ct));
            }
            return text.ToString();
        }

        [Fact]
        public async Task Secret_canary_absent_from_db_api_audit_logs()
        {
            await using var rig = await StartAsync();
            var id = IdOf(await rig.CreateAsync());
            await rig.Post($"/instances/{id}/validate");
            await rig.Post($"/instances/{id}/enable");
            rig.Control.ThrowOnValidate = true;
            await rig.Post($"/instances/{id}/disable");
            await rig.Post($"/instances/{id}/validate");
            await rig.SendAsync(rig.Admin, HttpMethod.Get, $"{rig.Base}/instances/{id}");
            await rig.SendAsync(rig.Admin, HttpMethod.Get, $"{rig.Base}/instances");
            // the plugin did resolve and echo the value, so the scan means something
            Assert.True(rig.Control.Validations >= 2);
            Assert.DoesNotContain(Canary, await EverythingTextAsync(rig));
            Assert.All(rig.Responses, r => Assert.DoesNotContain(Canary, r));
            Assert.Contains(rig.Logs.Entries, e => e.Message.Contains("Validating with ***"));
            Assert.DoesNotContain(rig.Logs.Entries, e => e.Message.Contains(Canary));
        }

        [Fact]
        public async Task Plugin_detail_echoing_secret_stored_scrubbed()
        {
            await using var rig = await StartAsync();
            var id = IdOf(await rig.CreateAsync());
            var (_, body) = await rig.Post($"/instances/{id}/validate");
            var detail = body.GetProperty("report").GetProperty("checks")[1].GetProperty("detail").GetString();
            Assert.Equal("signed in with ***", detail);
            var stored = await rig.TextAsync($"SELECT last_validation::text FROM integrations.integration_instances WHERE id = '{id}'");
            Assert.Contains("signed in with ***", stored);
            Assert.DoesNotContain(Canary, stored);
            // and a plugin text longer than 500 characters is cut
            Assert.All(body.GetProperty("report").GetProperty("checks").EnumerateArray(), c => Assert.True((c.GetProperty("detail").GetString() ?? "").Length <= 500));
        }
    }
}
