using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// The running <c>deploy/local</c> stack: its generated secrets, and helpers to send OTLP through the Collector,
    /// query ClickHouse, create a scratch PostgreSQL database and get Keycloak tokens. Integration tests call
    /// <see cref="RequireAvailable"/> first and are skipped when the stack is not up. They send through the
    /// <c>otel-collector-test</c> Collector (<c>--profile test</c>), which stamps <see cref="TenantKey"/>, so what they
    /// write stays out of the tenant the running API works on.
    /// </summary>
    public static class LocalStack
    {
        public const string TenantKey = "local-test";

        private static readonly Lazy<string?> Directory = new(() =>
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "deploy", "local");
                if (File.Exists(Path.Combine(candidate, ".env")))
                {
                    return candidate;
                }
            }
            return null;
        });

        private static readonly Lazy<Dictionary<string, string>> Env = new(() => Directory.Value is null ? [] :
            File.ReadAllLines(Path.Combine(Directory.Value, ".env"))
                .Where(l => l.Contains('=') && !l.TrimStart().StartsWith('#'))
                .Select(l => l.Split('=', 2)).ToDictionary(p => p[0].Trim(), p => p[1].Trim()));

        private static readonly Lazy<bool> Up = new(() => Directory.Value is not null
            && new[] { 5433, 8123, 14318, 8080 }.All(Listening));

        public static void RequireAvailable()
            => Assert.SkipUnless(Up.Value, "deploy/local stack is not running (scripts/init.sh; docker compose --profile test up -d --wait)");

        public static string Secret(string key) => Env.Value[key];

        public static string PostgresConnection(string database)
            => $"Host=127.0.0.1;Port=5433;Database={database};Username=juice;Password={Secret("POSTGRES_PASSWORD")}";

        public static string ClickHouseReader
            => $"Host=127.0.0.1;Port=8123;Username=platform_reader;Password={Secret("CLICKHOUSE_PLATFORM_PASSWORD")};Database=otel";

        public const string Authority = "http://localhost:8080/realms/juice";

        private static bool Listening(int port)
        {
            try
            {
                using var client = new TcpClient();
                return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(1)) && client.Connected;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ─── OTLP through the Collector ───────────────────────────────────────

        private static readonly Lazy<HttpClient> Otlp = new(() =>
        {
            var ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(Directory.Value!, "secrets", "tls", "ca.crt"));
            var handler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    // Trust only the stack's CA, as the hosts do with OTEL_EXPORTER_OTLP_CERTIFICATE.
                    RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    {
                        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
                        using var chain = new X509Chain();
                        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        chain.ChainPolicy.CustomTrustStore.Add(ca);
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        return chain.Build(new X509Certificate2(certificate));
                    },
                },
            };
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:14318") };
            var token = File.ReadAllText(Path.Combine(Directory.Value!, "secrets", "otlp-tokens")).Split(' ', '\n')[0].Trim();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        });

        /// <summary>Posts OTLP/JSON to the Collector (<c>traces</c>, <c>metrics</c> or <c>logs</c>).</summary>
        public static async Task SendAsync(string signal, JsonObject body)
        {
            using var response = await Otlp.Value.PostAsync($"/v1/{signal}",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
        }

        // ─── ClickHouse (admin, for waiting and seeding only) ─────────────────

        private static readonly Lazy<HttpClient> ClickHouse = new(() =>
        {
            var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8123") };
            client.DefaultRequestHeaders.Add("X-ClickHouse-User", "admin");
            client.DefaultRequestHeaders.Add("X-ClickHouse-Key", Secret("CLICKHOUSE_ADMIN_PASSWORD"));
            return client;
        });

        public static async Task<string> ClickHouseAsync(string sql)
        {
            using var response = await ClickHouse.Value.PostAsync("/", new StringContent(sql));
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, text);
            return text.Trim();
        }

        /// <summary>Waits until ClickHouse holds <paramref name="expected"/> spans of a service (exporter batches flush every second).</summary>
        public static async Task WaitForSpansAsync(string serviceName, int expected)
        {
            for (var i = 0; i < 100; i++)
            {
                if (int.Parse(await ClickHouseAsync($"SELECT count() FROM otel.otel_traces WHERE ServiceName = '{serviceName}'")) >= expected)
                {
                    return;
                }
                await Task.Delay(200);
            }
            Assert.Fail($"{expected} spans of {serviceName} did not reach ClickHouse");
        }

        // ─── PostgreSQL scratch database ──────────────────────────────────────

        /// <summary>A new, migrated database with the local tenant provisioned; dropped on dispose.</summary>
        public static async Task<ScratchDatabase> CreateDatabaseAsync(bool provisionTenant = true)
        {
            var name = $"juice_obs_test_{Guid.NewGuid():N}";
            await using (var admin = new NpgsqlConnection(PostgresConnection("juice_observability")))
            {
                await admin.OpenAsync();
                await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
                await create.ExecuteNonQueryAsync();
            }
            var database = new ScratchDatabase(name);
            await using var db = database.Context();
            await db.Database.MigrateAsync();
            if (provisionTenant)
            {
                db.Tenants.Add(new PlatformTenant { Key = TenantKey, DisplayName = TenantKey });
                await db.SaveChangesAsync();
            }
            return database;
        }

        // ─── Keycloak tokens ──────────────────────────────────────────────────

        private static readonly HttpClient Keycloak = new();
        private static readonly Dictionary<string, Task<string>> Tokens = [];

        /// <summary>
        /// An access token for a demo user from the <c>juice-platform-test</c> client, requested once per test run (tokens
        /// live 5 minutes) as a real client would. Many parallel password grants for one user trip Keycloak's
        /// brute-force detection and lock the user out.
        /// </summary>
        public static Task<string> TokenAsync(string user)
        {
            lock (Tokens)
            {
                if (!Tokens.TryGetValue(user, out var token) || token.IsFaulted)
                {
                    Tokens[user] = token = RequestTokenAsync(user);
                }
                return token;
            }
        }

        private static async Task<string> RequestTokenAsync(string user)
        {
            using var response = await Keycloak.PostAsync($"{Authority}/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "juice-platform-test",
                ["username"] = user,
                ["password"] = Secret("KEYCLOAK_DEMO_PASSWORD"),
            }));
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(response.IsSuccessStatusCode, json.ToString());
            return json.GetProperty("access_token").GetString()!;
        }
    }

    public sealed class ScratchDatabase(string name) : IAsyncDisposable
    {
        public string Name { get; } = name;

        public string ConnectionString => LocalStack.PostgresConnection(Name);

        public PlatformDbContext Context()
        {
            var options = new DbContextOptionsBuilder<PlatformDbContext>();
            PlatformDbContext.Configure(options, ConnectionString);
            return new PlatformDbContext(options.Options);
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(LocalStack.PostgresConnection("juice_observability"));
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {Name} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
