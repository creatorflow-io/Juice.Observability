using Juice.Observability.Agents;
using Juice.Observability.Api;

// The reference host: the platform packages plus Juice Console served from wwwroot on the same origin. A customer's own
// host makes the same three calls.
// `notifications migrate` was removed in 2.0 (the legacy webhooks it migrated are gone): it says so and exits 2.
if (RemovedCommands.IsRemoved(args))
{
    Environment.ExitCode = await RemovedCommands.RunAsync(args, Console.Error);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJuiceObservabilityFiles();
builder.Services.AddJuiceObservability(builder.Configuration);
// The agent listener, when Agents:Enabled: its own port with TLS and client certificates. A customer's host behind its own TLS proxy passes it through.
builder.WebHost.ConfigureKestrel((context, kestrel) => kestrel.ListenForAgents(context.Configuration));

var app = builder.Build();

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
// Any other path outside /api, /health and the notifications admin API is a Console route. API paths keep routing's own answers (404, 405).
app.MapFallbackToFile("{*path:nonfile:regex(^(?!(api|health|notifications/api)(/|$)).*$)}", "index.html").AllowAnonymous();
// The notification pages' links end in keys with dots (types/observability.alert.firing), which `nonfile` takes for files: under their mount
// every path is a Console route (their chunks load from the root).
app.MapFallbackToFile("alerts/notifications/{**path}", "index.html").AllowAnonymous();

app.Run();

/// <summary>Exposed for <c>WebApplicationFactory</c> in tests.</summary>
public partial class Program;
