using Juice.Observability.Api;
using Juice.Observability.Api.Integrations;
using Juice.Observability.Api.Integrations.Health;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Juice.Observability.Tests.Integrations.Sample;
using Juice.Observability.Tests.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests.Integrations.Health
{
    /// <summary>
    /// The platform on a scratch database with the <c>sample</c> module and the integrations tables, whose passes the test runs by hand (no hosted service
    /// starts, so no lease and no timer): what a pass does is read off the database afterwards.
    /// </summary>
    internal sealed class IntegrationHealthHost : IAsyncDisposable
    {
        public const string Canary = "canary-5f0c1d2e-secret";

        private readonly ScratchDatabase _database;

        private IntegrationHealthHost(ScratchDatabase database, ModuleTestHost host)
        {
            _database = database;
            Host = host;
        }

        public ModuleTestHost Host { get; }

        public IServiceProvider Services => Host.App.Services;

        public SampleJobsControl Control => Services.GetRequiredService<SampleJobsControl>();

        public static async Task<IntegrationHealthHost> StartAsync(IReadOnlyDictionary<string, string?>? settings = null, bool sample = true, LogCapture? logs = null)
        {
            LocalStack.RequireAvailable();
            Environment.SetEnvironmentVariable("JUICE_INTEGRATION_B_TOKEN", Canary);
            var database = await LocalStack.CreateDatabaseAsync();
            var host = await ModuleTestHost.StartAsync(b =>
            {
                if (logs is not null)
                {
                    b.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
                }
                if (sample)
                {
                    b.AddObservabilityModule<SampleModule>();
                }
            }, database, hostedServices: false, settings);
            var result = new IntegrationHealthHost(database, host);
            // The module's own migration is slice A's; the tables are what its model says, which is all a pass needs.
            await using var scope = host.App.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = db.Database.GenerateCreateScript();
            await command.ExecuteNonQueryAsync();
            return result;
        }

        public PlatformDbContext Platform() => _database.Context();

        public async Task<Guid> AddAsync(IntegrationState state = IntegrationState.Enabled, string plugin = SampleJobsPlugin.Key, string name = "jobs", int configVersion = 1,
            string? config = null, DateTimeOffset? nextRunAt = null)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
            var instance = new IntegrationInstance
            {
                TenantId = Host.TenantId, PluginKey = plugin, Name = name + Guid.NewGuid().ToString("N")[..6], Config = config ?? """{"baseUrl":"https://jobs.example"}""",
                ConfigVersion = configVersion, SecretRefs = """{"token":"env:JUICE_INTEGRATION_B_TOKEN"}""", IntervalSeconds = 300, State = state,
                NextRunAt = nextRunAt ?? DateTimeOffset.UtcNow.AddSeconds(-5), CreatedBy = "test", UpdatedBy = "test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Instances.Add(instance);
            await db.SaveChangesAsync();
            return instance.Id;
        }

        public async Task<IntegrationInstance> GetAsync(Guid id)
        {
            await using var scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>().Instances.AsNoTracking().SingleAsync(i => i.Id == id);
        }

        public async Task<List<IntegrationCheckpointRow>> CheckpointsAsync(Guid id)
        {
            await using var scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>().Checkpoints.AsNoTracking().Where(c => c.InstanceId == id).ToListAsync();
        }

        /// <summary>Makes the instance due now, as the passing of its backoff would.</summary>
        public async Task MakeDueAsync(Guid id)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
            var past = DateTimeOffset.UtcNow.AddSeconds(-5);
            await db.Instances.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.NextRunAt, (DateTimeOffset?)past));
        }

        public async Task UpdateAsync(Guid id, Action<IntegrationInstance> change)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IntegrationsDbContext>();
            var instance = await db.Instances.SingleAsync(i => i.Id == id);
            change(instance);
            await db.SaveChangesAsync();
        }

        public async Task<LeasedPassOutcome> PassAsync(CancellationToken leaseLost = default, bool retaken = false)
        {
            await using var scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IntegrationsWorker>().RunOnceAsync(new LeasedPass("integrations", 1, retaken, leaseLost), TestContext.Current.CancellationToken);
        }

        /// <summary>The integration alerts: open and resolved.</summary>
        public async Task<List<Alert>> AlertsAsync()
        {
            await using var db = Platform();
            var rules = db.Rules.Where(r => r.Key == IntegrationHealthRule.Key).Select(r => r.Id);
            return await db.Alerts.AsNoTracking().Where(a => rules.Contains(a.RuleId)).OrderBy(a => a.BreachStartedAt).ToListAsync();
        }

        public async Task<int> RuleCountAsync()
        {
            await using var db = Platform();
            return await db.Rules.CountAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await _database.DisposeAsync();
        }
    }
}
