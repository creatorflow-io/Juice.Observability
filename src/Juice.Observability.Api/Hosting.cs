using System.Security.Claims;
using System.Text.RegularExpressions;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api
{
    /// <summary>Configuration section <c>Auth</c>: the deployment's OIDC provider (requirements §6.1).</summary>
    public sealed class AuthOptions
    {
        public const string Section = "Auth";

        /// <summary>The issuer, for example <c>http://localhost:8080/realms/juice</c>.</summary>
        public string Authority { get; set; } = "";

        public string Audience { get; set; } = "juice-platform";

        /// <summary>Only for a local identity provider over plain HTTP.</summary>
        public bool RequireHttpsMetadata { get; set; } = true;

        /// <summary>The claim naming the platform tenant the caller belongs to.</summary>
        public string TenantClaim { get; set; } = "platform_tenant";
    }

    public static class Roles
    {
        public const string Viewer = "viewer";
        public const string Operator = "operator";
        public const string Admin = "admin";
    }

    public static class Policies
    {
        /// <summary>Read inventory, health and alerts: any platform role.</summary>
        public const string Read = "platform.read";
    }

    public static class HostingExtensions
    {
        public static IServiceCollection AddPlatformAuthentication(this IServiceCollection services, IConfiguration configuration)
        {
            var auth = configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
            services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = auth.Authority;
                    options.Audience = auth.Audience;
                    options.RequireHttpsMetadata = auth.RequireHttpsMetadata;
                    options.MapInboundClaims = false;
                    options.TokenValidationParameters.NameClaimType = "preferred_username";
                    options.TokenValidationParameters.RoleClaimType = "roles";
                    options.TokenValidationParameters.ValidateIssuer = true;
                    options.TokenValidationParameters.ValidateAudience = true;
                    options.TokenValidationParameters.ValidateLifetime = true;
                });
            services.AddAuthorizationBuilder()
                .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
                .AddPolicy(Policies.Read, p => p.RequireRole(Roles.Viewer, Roles.Operator, Roles.Admin));
            return services;
        }

        /// <summary>RFC 9457 problem details on every error, carrying the correlation id and trace id.</summary>
        public static IServiceCollection AddPlatformProblemDetails(this IServiceCollection services)
            => services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions["correlationId"] = CorrelationId.Get(context.HttpContext);
                context.ProblemDetails.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString()
                    ?? context.HttpContext.TraceIdentifier;
            });

        public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) => app.Use(async (context, next) =>
        {
            var id = CorrelationId.FromRequest(context.Request.Headers[CorrelationId.Header]);
            context.Items[CorrelationId.Header] = id;
            context.Response.Headers[CorrelationId.Header] = id;
            await next(context);
        });
    }

    public static partial class CorrelationId
    {
        public const string Header = "X-Correlation-Id";

        /// <summary>The caller's id when it is safe to echo (1–128 of <c>[A-Za-z0-9._-]</c>); otherwise a new one.</summary>
        public static string FromRequest(string? value)
            => value is { Length: > 0 and <= 128 } && Safe().IsMatch(value) ? value : Guid.CreateVersion7().ToString("N");

        public static string Get(HttpContext context)
            => context.Items.TryGetValue(Header, out var id) && id is string s ? s : context.TraceIdentifier;

        [GeneratedRegex("^[A-Za-z0-9._-]+$")]
        private static partial Regex Safe();
    }

    /// <summary>
    /// Ensures the one tenant of this deployment exists (requirements §4 step 6): creates it on first start, and stops the
    /// host when the database already holds a different tenant, so a mis-set <c>Platform:TenantKey</c> cannot attach
    /// telemetry of one tenant to another's records. Applies migrations first when <c>Database:Migrate</c> is true.
    /// </summary>
    public sealed partial class TenantProvisioner(IServiceScopeFactory scopes, IOptions<PlatformOptions> platform,
        IConfiguration configuration, ILogger<TenantProvisioner> logger) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var key = platform.Value.TenantKey;
            if (!TenantKey().IsMatch(key))
            {
                throw new InvalidOperationException($"Platform:TenantKey '{key}' must match {TenantKey()}");
            }
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            if (configuration.GetValue("Database:Migrate", false))
            {
                await db.Database.MigrateAsync(cancellationToken);
            }
            var tenants = await db.Tenants.AsNoTracking().ToListAsync(cancellationToken);
            if (tenants.Any(t => t.Key == key))
            {
                return;
            }
            if (tenants.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The database belongs to tenant '{tenants[0].Key}', not the configured '{key}'. A single-tenant deployment holds one tenant.");
            }
            var tenant = new PlatformTenant
            {
                Key = key,
                DisplayName = platform.Value.TenantDisplayName is { Length: > 0 } name ? name : key,
            };
            db.Tenants.Add(tenant);
            db.AuditRecords.Add(new AuditRecord
            {
                TenantId = tenant.Id, Actor = "system", Operation = "tenant.provision", TargetType = "tenant",
                TargetId = tenant.Id.ToString(), Outcome = AuditOutcome.Succeeded,
                Metadata = new() { ["key"] = key },
            });
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Provisioned platform tenant {TenantKey} ({TenantId})", key, tenant.Id);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
        private static partial Regex TenantKey();
    }

    internal sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("PostgreSQL is unreachable");
        }
    }

    internal static class ClaimsPrincipalExtensions
    {
        public static string Actor(this ClaimsPrincipal user) => user.Identity?.Name ?? user.FindFirstValue("sub") ?? "unknown";
    }
}
