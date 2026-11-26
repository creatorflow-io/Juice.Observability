using Juice.Observability.Agents;
using Juice.Observability.Api;

// The reference host: the platform packages plus Juice Console served from wwwroot on the same origin. A customer's own
// host makes the same three calls.
// `notifications migrate [--apply] [--tenant <id>]` (phase 5, M46b): the migration tool, run once, then the host exits.
var command = NotificationsMigrationCommand.Parse(args);
var builder = WebApplication.CreateBuilder(command is null ? args : []);
builder.Configuration.AddJuiceObservabilityFiles();
builder.Services.AddJuiceObservability(builder.Configuration);
// The agent listener, when Agents:Enabled: its own port with TLS and client certificates. A customer's host behind its own TLS proxy passes it through.
builder.WebHost.ConfigureKestrel((context, kestrel) => kestrel.ListenForAgents(context.Configuration));

var app = builder.Build();
if (command is not null)
{
    Environment.ExitCode = await command.RunAsync(app.Services, Console.Out, CancellationToken.None);
    return;
}

app.UseAgentListener();
app.UseCorrelationId();
app.UseExceptionHandler();
app.UseStatusCodePages();
// Juice Console (wwwroot, when the image includes it): static files on the API's own origin, never framed.
app.Use((context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    return next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapJuiceObservability();
// Any other path outside /api and /health is a Console route. API paths keep routing's own answers (404, 405).
app.MapFallbackToFile("{*path:nonfile:regex(^(?!(api|health)(/|$)).*$)}", "index.html").AllowAnonymous();

app.Run();

/// <summary>Exposed for <c>WebApplicationFactory</c> in tests.</summary>
public partial class Program;
