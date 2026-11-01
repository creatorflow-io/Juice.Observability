using Juice.Observability.Api;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Health;
using Juice.Observability.Rules;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
// Rules are deployment configuration: a version-controlled JSON file, loaded once at startup (requirements §6.1).
if (builder.Configuration["Rules:File"] is { Length: > 0 } rulesFile)
{
    builder.Configuration.AddJsonFile(Path.GetFullPath(rulesFile), optional: false, reloadOnChange: false);
}

builder.Services.AddDbContext<PlatformDbContext>(options =>
    PlatformDbContext.Configure(options, builder.Configuration.GetConnectionString("Platform")
        ?? throw new InvalidOperationException("ConnectionStrings:Platform is required")));
// Before the discovery worker: hosted services start in registration order, and discovery needs the tenant.
builder.Services.AddHostedService<TenantProvisioner>();
builder.Services.AddPlatformDiscovery(builder.Configuration);
builder.Services.AddPlatformHealth(builder.Configuration);
builder.Services.AddPlatformRules(builder.Configuration);
builder.Services.Configure<GrafanaOptions>(builder.Configuration.GetSection(GrafanaOptions.Section));
builder.Services.AddPlatformAuthentication(builder.Configuration);
builder.Services.AddPlatformProblemDetails();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

var app = builder.Build();

app.UseCorrelationId();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
app.MapInventoryEndpoints();

app.Run();

/// <summary>Exposed for <c>WebApplicationFactory</c> in tests.</summary>
public partial class Program;
