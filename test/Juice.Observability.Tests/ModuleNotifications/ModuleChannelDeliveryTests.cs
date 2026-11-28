using System.Collections.Concurrent;
using System.Text.Json;
using Juice.Notifications;
using Juice.Observability.Api;
using Juice.Observability.Data;
using Juice.Observability.Tests.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests.ModuleNotifications
{
    /// <summary>A module's channel in the embedded pipeline: its provider is called for a delivery the module's type planned through a route.</summary>
    [Trait("Milestone", "C2-M3")]
    public sealed class ModuleChannelDeliveryTests
    {
        private const string Channel = "notify-sample-pager";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>The module's provider: records what it is asked to send and says it delivered. A real one sends through the pipeline's destination policy client.</summary>
        public sealed class RecordingPagerProvider : INotificationProvider, ITargetAddresses
        {
            public static readonly ConcurrentQueue<(string Destination, string Content)> Sent = new();

            public string Channel => ModuleChannelDeliveryTests.Channel;

            public string Kind => "pager";

            public bool Idempotent => true;

            public bool RequiresSecret => false;

            public string? CheckSettings(string settingsJson) => null;

            public string? NormalizeTarget(string address) => string.IsNullOrWhiteSpace(address) ? null : address.Trim();

            public string TargetRule => "a pager address";

            public Task<SendResult> SendAsync(RenderedDelivery delivery, IntegrationContext integration, CancellationToken cancellationToken)
            {
                Sent.Enqueue((delivery.Destination, delivery.Content));
                return Task.FromResult(new SendResult(AttemptOutcome.Delivered, null, "pager-1", null, null));
            }
        }

        [Fact]
        public async Task Module_channel_delivers_through_route()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var receiver = await TestReceiver.StartAsync();
            var file = Path.Combine(Path.GetTempPath(), $"juice-obs-c2m3-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
            {
                tenants = new Dictionary<string, object>
                {
                    [LocalStack.TenantKey] = new
                    {
                        integrations = new Dictionary<string, object>
                        {
                            ["pager"] = new { channel = Channel, kind = "pager", settings = new { }, targets = new Dictionary<string, object> { ["oncall"] = new { address = receiver.Url } } },
                        },
                        allowlist = new object[] { new { destination = $"{receiver.Address}/32", ports = new[] { receiver.Port } } },
                        routes = new Dictionary<string, object>
                        {
                            ["pings"] = new { match = new { type = NotifySampleModule.Ping }, targets = new[] { "oncall" } },
                        },
                    },
                },
            }), Ct);
            try
            {
                var settings = ProvisionedHost.Settings(file).ToDictionary(p => p.Key, p => (string?)p.Value);
                settings["Notifications:Stages:Run"] = "true";
                using var _ = NotifySampleModule.Use(channels: s =>
                {
                    s.AddSingleton(new ChannelDefinition(Channel, AddressKind.None, ChannelAudience.SharedTargets, ContentShape.Body, true, true));
                    s.AddSingleton<INotificationProvider, RecordingPagerProvider>();
                });
                await using var host = await ModuleTestHost.StartAsync(b => b.AddObservabilityModule<NotifySampleModule>(), database, hostedServices: true, settings: settings);
                Assert.True(host.App.Services.GetRequiredService<Juice.Observability.Notifications.PipelineStart>().Failure is null, host.App.Services.GetRequiredService<Juice.Observability.Notifications.PipelineStart>().Failure);

                var marker = Guid.NewGuid();
                await using (var scope = host.App.Services.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                    await using var transaction = await db.Database.BeginTransactionAsync(Ct);
                    await scope.ServiceProvider.GetRequiredService<INotificationClient>().SendAsync(new NotificationToSend(NotifySampleModule.Ping, $"c2m3:{marker}")
                    {
                        CorrelationKey = $"c2m3:{marker}",
                        Data = JsonSerializer.SerializeToElement(new { marker }),
                    }, Ct);
                    await transaction.CommitAsync(Ct);
                }

                await ProvisionedHost.WaitAsync(() => RecordingPagerProvider.Sent.Any(s => s.Content.Contains(marker.ToString())), "the module's provider to be asked to send");
                var sent = RecordingPagerProvider.Sent.First(s => s.Content.Contains(marker.ToString()));
                Assert.Equal(receiver.Url, sent.Destination);
            }
            finally
            {
                File.Delete(file);
            }
        }
    }
}
