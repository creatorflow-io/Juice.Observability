using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Juice.Observability.Health.Delivery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Juice.Observability.Tests
{
    /// <summary>A webhook receiver on this machine's private address. Answers with the given status codes in turn, then 200.</summary>
    internal sealed class TestReceiver : IAsyncDisposable
    {
        public sealed record Received(string Id, string Timestamp, string Signature, string Body);

        private readonly WebApplication _app;
        private readonly ConcurrentQueue<int> _statuses;
        public ConcurrentQueue<Received> Requests { get; } = new();
        public IPAddress Address { get; }
        public int Port { get; private set; }
        public string Url => $"http://{Address}:{Port}/hook";
        private bool _disposed;

        private TestReceiver(IPAddress address, int[] statuses)
        {
            Address = address;
            _statuses = new(statuses);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls($"http://{address}:0");
            builder.Logging.ClearProviders();
            _app = builder.Build();
            _app.MapPost("/hook", async (HttpRequest request) =>
            {
                using var reader = new StreamReader(request.Body);
                Requests.Enqueue(new Received(request.Headers[WebhookSigner.IdHeader]!, request.Headers[WebhookSigner.TimestampHeader]!,
                    request.Headers[WebhookSigner.SignatureHeader]!, await reader.ReadToEndAsync()));
                return Results.StatusCode(_statuses.TryDequeue(out var status) ? status : 200);
            });
            _app.MapPost("/redirect", () =>
            {
                Requests.Enqueue(new Received("", "", "", ""));
                return Results.Redirect("http://169.254.169.254/latest/meta-data");
            });
        }

        public static async Task<TestReceiver> StartAsync(params int[] statuses)
        {
            var address = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && DestinationPolicy.Classify(a) == DestinationClass.Private);
            Assert.SkipWhen(address is null, "no private IPv4 address on this machine for the webhook receiver");
            var receiver = new TestReceiver(address!, statuses);
            await receiver._app.StartAsync();
            receiver.Port = new Uri(receiver._app.Urls.First()).Port;
            return receiver;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
