using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// A host with the inbox off (the default) needs neither SignalR nor its connection stack: said of a host of its own, in its own process,
    /// since this process's inbox tests load them. No test of this class touches a SignalR type.
    /// </summary>
    [Trait("Milestone", "M54")]
    public sealed class InboxOffProcessTests
    {
        [Theory]
        [InlineData(false)]
        // The control: the same probe sees SignalR when the inbox is on.
        [InlineData(true)]
        public async Task InboxOff_HostProcess_LoadsNoSignalR_NoConnectionStack(bool inbox)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var port = FreePort();
            var dir = AppContext.BaseDirectory;
            var info = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir,
            };
            foreach (var arg in new[] { "exec", "--runtimeconfig", Path.Combine(dir, "Juice.Observability.Tests.runtimeconfig.json"),
                "--depsfile", Path.Combine(dir, "Juice.Observability.Tests.deps.json"), Path.Combine(dir, "Juice.Observability.Host.dll") })
            {
                info.ArgumentList.Add(arg);
            }
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}", ["ConnectionStrings__Platform"] = database.ConnectionString, ["Platform__TenantKey"] = LocalStack.TenantKey,
                ["ClickHouse__ConnectionString"] = LocalStack.ClickHouseReader, ["Auth__Authority"] = LocalStack.Authority, ["Auth__RequireHttpsMetadata"] = "false",
                ["Database__Migrate"] = "true", ["Discovery__Enabled"] = "false", ["Health__Enabled"] = "false", ["Rules__Enabled"] = "false",
                ["Notifications__Stages__Run"] = "false", ["Notifications__ConsoleUrl"] = "https://console.example.com",
                ["Notifications__Inbox__Enabled"] = inbox ? "true" : "false",
            })
            {
                info.Environment[key] = value;
            }
            using var process = new Process { StartInfo = info };
            // Drained as it comes (a chatty host would block on a full pipe), keeping the last lines for a failure's message.
            var tail = new System.Collections.Concurrent.ConcurrentQueue<string>();
            void Keep(object sender, DataReceivedEventArgs e)
            {
                if (e.Data is null) return;
                tail.Enqueue(e.Data);
                while (tail.Count > 40 && tail.TryDequeue(out _)) { }
            }
            process.OutputDataReceived += Keep;
            process.ErrorDataReceived += Keep;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
                var ready = false;
                for (var i = 0; i < 150 && !ready && !process.HasExited; i++)
                {
                    try
                    {
                        ready = (await http.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode == HttpStatusCode.OK;
                    }
                    catch (HttpRequestException)
                    {
                    }
                    if (!ready) await Task.Delay(200, TestContext.Current.CancellationToken);
                }
                Assert.True(ready, $"the host didn't start ({(process.HasExited ? "exited" : "timed out")}); its last output:\n{string.Join('\n', tail)}");
                // The inbox isn't mapped, and the process has loaded none of it.
                var loaded = process.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToList();
                Assert.Contains(loaded, n => n.StartsWith("Juice.Observability.Api", StringComparison.OrdinalIgnoreCase));
                bool SignalR(string n) => n.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("Microsoft.AspNetCore.Http.Connections", StringComparison.OrdinalIgnoreCase);
                if (inbox) Assert.Contains(loaded, SignalR);
                else Assert.DoesNotContain(loaded, SignalR);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
