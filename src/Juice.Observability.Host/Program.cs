using Juice.Observability.Api;

// The reference host: the platform packages plus Juice Console served from wwwroot on the same origin. A customer's own
// host makes the same three calls.
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJuiceObservabilityFiles();
builder.Services.AddJuiceObservability(builder.Configuration);

var app = builder.Build();

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
