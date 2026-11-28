using System.Text.Json;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Juice.Observability.Api.Integrations
{
    /// <summary>
    /// <c>/api/v1/tenants/{tenantId}/integrations</c>: plugins, and instances with an admin-only, audited lifecycle (decisions 5 and 6). Every query filters on
    /// the route tenant; every mutation needs the admin role and the deployment's own tenant. Config, scope and references reach only an admin reading one instance.
    /// </summary>
    internal static class IntegrationEndpoints
    {
        private const string TargetType = "integration-instance";

        public static RouteGroupBuilder MapIntegrationEndpoints(this RouteGroupBuilder tenant)
        {
            // The module's metadata lets the instance mutations use IModuleMutations (audit and idempotency in the module's transaction).
            var group = tenant.MapGroup("/integrations").WithMetadata(new ModuleEndpointMetadata("integrations"));
            group.MapGet("/plugins", (IIntegrationPluginRegistry plugins) => Results.Ok(plugins.All.Select(IntegrationMapping.ToDto).ToList()));
            group.MapGet("/instances", ListAsync);
            group.MapGet("/instances/{id:guid}", GetAsync);
            group.MapPost("/instances", CreateAsync);
            group.MapPut("/instances/{id:guid}", UpdateAsync);
            group.MapDelete("/instances/{id:guid}", DeleteAsync);
            group.MapPost("/instances/{id:guid}/validate", ValidateAsync);
            group.MapPost("/instances/{id:guid}/enable", EnableAsync);
            group.MapPost("/instances/{id:guid}/disable", DisableAsync);
            return group;
        }

        private static IResult Problem(int status, string title, string detail) => Mutation.Problem(status, title, detail);

        private static IResult NotFound(Guid id) => Problem(StatusCodes.Status404NotFound, "Integration not found", $"No integration instance {id:N}.");

        private static IResult Invalid(Dictionary<string, List<string>> errors)
            => Results.ValidationProblem(errors.ToDictionary(e => e.Key, e => e.Value.ToArray()), title: "Invalid request");

        // ── Reads ──────────────────────────────────────────────────────────────

        private static async Task<IResult> ListAsync(Guid tenantId, string? state, string? plugin, int? limit, string? cursor, IntegrationsDbContext db,
            IIntegrationPluginRegistry plugins, TimeProvider clock, CancellationToken ct)
        {
            if (Paging.Validate(limit, cursor, out var take, out var after) is { } bad)
            {
                return bad;
            }
            IntegrationState? wanted = null;
            if (state is not null)
            {
                if (!Enum.TryParse<IntegrationState>(state, true, out var parsed) || !Enum.IsDefined(parsed))
                {
                    return Problem(StatusCodes.Status400BadRequest, "Invalid state", "state is one of Configured, Validated, Enabled, Degraded, Disabled, Failed.");
                }
                wanted = parsed;
            }
            var query = db.Instances.AsNoTracking().Where(i => i.TenantId == tenantId);
            if (wanted is { } s)
            {
                query = query.Where(i => i.State == s);
            }
            if (!string.IsNullOrEmpty(plugin))
            {
                query = query.Where(i => i.PluginKey == plugin);
            }
            if (after is not null)
            {
                var last = after.After;
                query = query.Where(i => i.Id.CompareTo(last) > 0);
            }
            var rows = await query.OrderBy(i => i.Id).Take(take + 1).ToListAsync(ct);
            var now = clock.GetUtcNow();
            var items = rows.Take(take).Select(i => IntegrationMapping.ToDto(i, plugins, now, detail: false)).ToList();
            return Results.Ok(new Page<IntegrationInstanceDto>(items, rows.Count > take ? new PageCursor("i", rows[take - 1].Id).Encode() : null));
        }

        private static async Task<IResult> GetAsync(Guid tenantId, Guid id, HttpContext http, IntegrationsDbContext db, IIntegrationPluginRegistry plugins, TimeProvider clock, CancellationToken ct)
        {
            var instance = await db.Instances.AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == id, ct);
            if (instance is null)
            {
                return NotFound(id);
            }
            http.Response.Headers.ETag = IntegrationMapping.Etag(instance);
            return Results.Ok(IntegrationMapping.ToDto(instance, plugins, clock.GetUtcNow(), detail: http.User.IsInRole(CoreRoles.Admin)));
        }

        // ── Mutations ──────────────────────────────────────────────────────────

        /// <summary>The admin role, then the deployment's own tenant: a refusal of either is audited as Denied. Nothing is written for a caller who passes both.</summary>
        private static async Task<(IModuleMutation? Mutation, IResult? Refused)> BeginAsync(HttpContext http, Guid tenantId, string operation, string? target, CancellationToken ct)
        {
            var mutation = http.RequestServices.GetRequiredService<IModuleMutations>().Begin(http, tenantId, $"integrations.instance.{operation}", TargetType);
            if (await mutation.RequireAsync(target, ct, CoreRoles.Admin) is { } denied)
            {
                return (null, denied);
            }
            var deployment = http.RequestServices.GetRequiredService<IOptions<PlatformOptions>>().Value.TenantKey;
            if ((string?)http.Items[TelemetryEndpoints.TenantKeyItem] != deployment)
            {
                mutation.Audit(ModuleAuditOutcome.Denied, target, new Dictionary<string, string> { ["reason"] = "tenant" });
                await mutation.SaveAsync(ct);
                return (null, Problem(StatusCodes.Status403Forbidden, "Forbidden", "Integrations are changed in the deployment's own tenant."));
            }
            return (mutation, null);
        }

        private static Dictionary<string, string> AuditData(IntegrationInstance i) => new()
        {
            ["plugin"] = i.PluginKey, ["state"] = i.State.ToString(), ["hash"] = i.ValidatedHash ?? IntegrationHash.Of(i),
        };

        /// <summary>Saves the module's rows with the audit record, and turns a lost race (concurrency token) or a taken name into the refusal.</summary>
        private static async Task<IResult?> SaveAsync(IModuleMutation mutation, int staleStatus, string staleTitle, CancellationToken ct)
        {
            try
            {
                await mutation.SaveAsync(ct);
                return null;
            }
            catch (DbUpdateConcurrencyException)
            {
                return Problem(staleStatus, staleTitle, "The integration changed since you read it. Read it again.");
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Problem(StatusCodes.Status409Conflict, "Name taken", "An integration with this name exists in the tenant.");
            }
        }

        private static async Task<IResult> CreateAsync(Guid tenantId, IntegrationInstanceRequest request, HttpContext http, IntegrationsDbContext db, IIntegrationPluginRegistry plugins,
            IOptions<IntegrationsOptions> options, TimeProvider clock, CancellationToken ct)
        {
            var (mutation, refused) = await BeginAsync(http, tenantId, "create", null, ct);
            if (mutation is null)
            {
                return refused!;
            }
            if (string.IsNullOrEmpty(request.PluginKey))
            {
                return Invalid(new() { ["pluginKey"] = ["is required"] });
            }
            if (!plugins.TryGet(request.PluginKey, out var descriptor))
            {
                return Problem(StatusCodes.Status409Conflict, "Plugin not installed", $"No integration plugin '{request.PluginKey}' is installed.");
            }
            var (normalized, errors) = Normalize(request, descriptor, options.Value);
            if (normalized is null)
            {
                return Invalid(errors);
            }

            await mutation.BeginTransactionAsync(ct);
            if (await mutation.ReplayAsync("POST /instances", request, ct) is { } replay)
            {
                return replay;
            }
            if (await db.Instances.CountAsync(i => i.TenantId == tenantId, ct) >= options.Value.MaxInstances)
            {
                return Problem(StatusCodes.Status409Conflict, "Too many integrations", $"A tenant has at most {options.Value.MaxInstances} integration instances.");
            }
            var key = normalized.Name.ToLowerInvariant();
            if (await db.Instances.AnyAsync(i => i.TenantId == tenantId && i.NameKey == key, ct))
            {
                return Problem(StatusCodes.Status409Conflict, "Name taken", "An integration with this name exists in the tenant.");
            }
            var now = clock.GetUtcNow();
            var instance = new IntegrationInstance
            {
                TenantId = tenantId, PluginKey = descriptor.Key, Name = normalized.Name, Config = normalized.Config, ConfigVersion = descriptor.Config.Version,
                SecretRefs = normalized.Refs, Scope = normalized.Scope, IntervalSeconds = normalized.Interval, State = IntegrationState.Configured,
                CreatedBy = mutation.Actor, CreatedAt = now, UpdatedBy = mutation.Actor, UpdatedAt = now,
            };
            db.Instances.Add(instance);
            mutation.Audit(ModuleAuditOutcome.Succeeded, instance.Id.ToString(), AuditData(instance));
            var dto = IntegrationMapping.ToDto(instance, plugins, now, detail: true);
            var result = mutation.Remember(StatusCodes.Status201Created, dto, now);
            if (await SaveAsync(mutation, StatusCodes.Status409Conflict, "Integration changed", ct) is { } problem)
            {
                return problem;
            }
            http.Response.Headers.Location = $"{http.Request.Path}/{instance.Id}";
            http.Response.Headers.ETag = IntegrationMapping.Etag(instance);
            return result;
        }

        private static async Task<IResult> UpdateAsync(Guid tenantId, Guid id, IntegrationInstanceRequest request, HttpContext http, IntegrationsDbContext db,
            IIntegrationPluginRegistry plugins, IOptions<IntegrationsOptions> options, TimeProvider clock, CancellationToken ct)
        {
            var (mutation, refused) = await BeginAsync(http, tenantId, "update", id.ToString(), ct);
            if (mutation is null)
            {
                return refused!;
            }
            var instance = await db.Instances.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == id, ct);
            if (instance is null)
            {
                return NotFound(id);
            }
            if (PreconditionFailure(http, instance) is { } precondition)
            {
                return precondition;
            }
            if (!plugins.TryGet(instance.PluginKey, out var descriptor))
            {
                return Problem(StatusCodes.Status409Conflict, "Plugin not installed", $"The plugin '{instance.PluginKey}' is not installed, so its configuration can't be checked.");
            }
            if (request.PluginKey is not null && request.PluginKey != instance.PluginKey)
            {
                return Invalid(new() { ["pluginKey"] = ["can't be changed"] });
            }
            var (normalized, errors) = Normalize(request, descriptor, options.Value);
            if (normalized is null)
            {
                return Invalid(errors);
            }
            var key = normalized.Name.ToLowerInvariant();
            if (await db.Instances.AnyAsync(i => i.TenantId == tenantId && i.NameKey == key && i.Id != id, ct))
            {
                return Problem(StatusCodes.Status409Conflict, "Name taken", "An integration with this name exists in the tenant.");
            }
            var definitionChanged = normalized.Config != IntegrationJson.Canonical(instance.Config) || normalized.Scope != IntegrationJson.Canonical(instance.Scope)
                || normalized.Refs != IntegrationJson.Canonical(instance.SecretRefs) || normalized.Interval != instance.IntervalSeconds || descriptor.Config.Version != instance.ConfigVersion;
            var now = clock.GetUtcNow();
            if (!definitionChanged && normalized.Name == instance.Name)
            {
                http.Response.Headers.ETag = IntegrationMapping.Etag(instance);
                return Results.Ok(IntegrationMapping.ToDto(instance, plugins, now, detail: true));
            }
            instance.Name = normalized.Name;
            if (definitionChanged)
            {
                // The change makes whatever was validated stale and stops runs: the version bump drops a run's outcome, the token and schedule go with it.
                instance.Config = normalized.Config;
                instance.ConfigVersion = descriptor.Config.Version;
                instance.Scope = normalized.Scope;
                instance.SecretRefs = normalized.Refs;
                instance.IntervalSeconds = normalized.Interval;
                instance.State = IntegrationState.Configured;
                instance.StateReason = null;
                instance.ValidatedHash = null;
                instance.LastValidation = null;
                instance.RunToken = null;
                instance.NextRunAt = null;
                instance.ConsecutiveFailures = 0;
                instance.LastFailure = null;
                instance.LastFailureDetail = null;
            }
            Touch(instance, mutation.Actor, now);
            mutation.Audit(ModuleAuditOutcome.Succeeded, id.ToString(), AuditData(instance));
            if (await SaveAsync(mutation, StatusCodes.Status412PreconditionFailed, "Integration changed", ct) is { } problem)
            {
                return problem;
            }
            http.Response.Headers.ETag = IntegrationMapping.Etag(instance);
            return Results.Ok(IntegrationMapping.ToDto(instance, plugins, now, detail: true));
        }

        private static async Task<IResult> DeleteAsync(Guid tenantId, Guid id, HttpContext http, IntegrationsDbContext db, CancellationToken ct)
        {
            var (mutation, refused) = await BeginAsync(http, tenantId, "delete", id.ToString(), ct);
            if (mutation is null)
            {
                return refused!;
            }
            var instance = await db.Instances.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == id, ct);
            if (instance is null)
            {
                return NotFound(id);
            }
            if (PreconditionFailure(http, instance) is { } precondition)
            {
                return precondition;
            }
            if (instance.State is not (IntegrationState.Configured or IntegrationState.Validated or IntegrationState.Disabled))
            {
                return Problem(StatusCodes.Status409Conflict, "Integration is running", "Disable the integration before deleting it.");
            }
            // The checkpoints go with it: the foreign key cascades.
            db.Instances.Remove(instance);
            mutation.Audit(ModuleAuditOutcome.Succeeded, id.ToString(), AuditData(instance));
            if (await SaveAsync(mutation, StatusCodes.Status412PreconditionFailed, "Integration changed", ct) is { } problem)
            {
                return problem;
            }
            return Results.NoContent();
        }

        /// <summary><c>If-Match</c> is required (428) and must be the instance's current version (412).</summary>
        private static IResult? PreconditionFailure(HttpContext http, IntegrationInstance instance)
        {
            var match = http.Request.Headers.IfMatch.ToString();
            if (match.Length == 0)
            {
                return Problem(StatusCodes.Status428PreconditionRequired, "If-Match required", "Send the ETag you read in If-Match.");
            }
            return match.Trim().Trim('"') == instance.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ? null
                : Problem(StatusCodes.Status412PreconditionFailed, "Integration changed", "Someone changed this integration since you read it. Read it again.");
        }

        private static void Touch(IntegrationInstance instance, string actor, DateTimeOffset now)
        {
            instance.Version++;
            instance.UpdatedBy = actor;
            instance.UpdatedAt = now;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private static async Task<IResult> ValidateAsync(Guid tenantId, Guid id, HttpContext http, IntegrationsDbContext db, IIntegrationPluginRegistry plugins,
            IOptions<IntegrationsOptions> options, TimeProvider clock, CancellationToken ct)
        {
            var (mutation, refused) = await BeginAsync(http, tenantId, "validate", id.ToString(), ct);
            if (mutation is null)
            {
                return refused!;
            }
            var instance = await db.Instances.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == id, ct);
            if (instance is null)
            {
                return NotFound(id);
            }
            if (instance.State is not (IntegrationState.Configured or IntegrationState.Validated or IntegrationState.Disabled or IntegrationState.Failed))
            {
                return Problem(StatusCodes.Status409Conflict, "Wrong state", $"A {instance.State} integration can't be validated; disable it first.");
            }
            if (!plugins.TryGet(instance.PluginKey, out var descriptor))
            {
                return Problem(StatusCodes.Status409Conflict, "Plugin not installed", $"The plugin '{instance.PluginKey}' is not installed.");
            }
            if (await FailOnVersionMismatchAsync(instance, descriptor, mutation, clock, ct) is { } mismatch)
            {
                return mismatch;
            }

            // What this validation vouches for, taken before the plugin runs; no transaction is open while it does.
            var hash = IntegrationHash.Of(instance);
            var secrets = new List<string>();
            var report = await RunValidationAsync(instance, descriptor, http, db, options.Value, secrets, ct);
            var now = clock.GetUtcNow();
            var validation = new ValidationDto(report.Passed, report.Checks, now);

            instance.LastValidation = JsonSerializer.Serialize(validation, JsonSerializerOptions.Web);
            instance.State = report.Passed ? IntegrationState.Validated : IntegrationState.Configured;
            instance.StateReason = report.Passed ? null : "Validation failed";
            instance.ValidatedHash = report.Passed ? hash : null;
            instance.RunToken = null;
            instance.NextRunAt = null;
            if (report.Passed)
            {
                instance.ConsecutiveFailures = 0;
                instance.LastFailure = null;
                instance.LastFailureDetail = null;
            }
            Touch(instance, mutation.Actor, now);
            mutation.Audit(ModuleAuditOutcome.Succeeded, id.ToString(), AuditData(instance));
            // A racing update moved the version: this validated what is no longer there, and saves nothing.
            if (await SaveAsync(mutation, StatusCodes.Status409Conflict, "Integration changed", ct) is { } problem)
            {
                return problem;
            }
            http.Response.Headers.ETag = IntegrationMapping.Etag(instance);
            return Results.Ok(new IntegrationValidateResponse(IntegrationMapping.ToDto(instance, plugins, now, detail: true), validation));
        }

        private static async Task<ValidationReport> RunValidationAsync(IntegrationInstance instance, IntegrationDescriptor descriptor, HttpContext http, IntegrationsDbContext db,
            IntegrationsOptions options, List<string> secrets, CancellationToken ct)
        {
            IntegrationContext? context = null;
            ValidationReport? report = null;
            ValidationCheck? failure = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.ValidateTimeout);
            try
            {
                var checkpoints = new RunCheckpoints(async (stream, token) =>
                {
                    var row = await db.Checkpoints.AsNoTracking().SingleOrDefaultAsync(c => c.InstanceId == instance.Id && c.Stream == stream, token);
                    return row is null ? null : new IntegrationCheckpoint(row.Stream, row.Watermark, row.Cursor);
                }, readOnly: true);
                context = IntegrationContextFactory.Create(instance, http.RequestServices, checkpoints);
                var plugin = http.RequestServices.GetRequiredService<IIntegrationPluginRegistry>().Resolve(http.RequestServices, descriptor.Key);
                report = await plugin.ValidateAsync(context, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failure = new ValidationCheck("timeout", false, $"The plugin did not answer within {options.ValidateTimeout.TotalSeconds:0} seconds.");
            }
            catch (IntegrationSecretException e)
            {
                failure = new ValidationCheck("secrets", false, e.Message);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failure = new ValidationCheck("plugin", false, $"The plugin failed: {e.GetType().Name}: {e.Message}");
            }
            // Whatever the plugin said, and whatever it failed with, is cleaned of the secrets it resolved and capped.
            var values = context is null ? [] : IntegrationContextFactory.ResolvedValues(context);
            var checks = (report?.Checks ?? []).Concat(failure is null ? [] : [failure]).Take(20)
                .Select(c => new ValidationCheck(IntegrationScrubber.Clean(c?.Name, values, 64) ?? "check", c?.Passed ?? false, IntegrationScrubber.Clean(c?.Detail, values))).ToList();
            var passed = failure is null && report is { Passed: true } && checks.All(c => c.Passed);
            return new ValidationReport(passed, checks);
        }

        private static async Task<IResult?> FailOnVersionMismatchAsync(IntegrationInstance instance, IntegrationDescriptor descriptor, IModuleMutation mutation, TimeProvider clock,
            CancellationToken ct)
        {
            if (instance.ConfigVersion == descriptor.Config.Version)
            {
                return null;
            }
            var reason = $"config version {instance.ConfigVersion}, plugin expects {descriptor.Config.Version}";
            instance.State = IntegrationState.Failed;
            instance.StateReason = reason;
            instance.ValidatedHash = null;
            instance.RunToken = null;
            Touch(instance, mutation.Actor, clock.GetUtcNow());
            mutation.Audit(ModuleAuditOutcome.Failed, instance.Id.ToString(), AuditData(instance));
            await SaveAsync(mutation, StatusCodes.Status409Conflict, "Integration changed", ct);
            return Problem(StatusCodes.Status409Conflict, "Config version mismatch", reason + ". Edit and save the configuration, then validate it again.");
        }

        private static async Task<IResult> EnableAsync(Guid tenantId, Guid id, HttpContext http, IntegrationsDbContext db, IIntegrationPluginRegistry plugins, TimeProvider clock,
            CancellationToken ct)
        {
            var (mutation, refused) = await BeginAsync(http, tenantId, "enable", id.ToString(), ct);
            if (mutation is null)
            {
                return refused!;
            }
            var instance = await db.Instances.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == id, ct);
            if (instance is null)
            {
                return NotFound(id);
            }
            if (!plugins.TryGet(instance.PluginKey, out var descriptor))
            {
                return Problem(StatusCodes.Status409Conflict, "Plugin not installed", $"The plugin '{instance.PluginKey}' is not installed.");
            }
            if (await FailOnVersionMismatchAsync(instance, descriptor, mutation, clock, ct) is { } mismatch)
            {
                return mismatch;
            }
            // Validated, or Disabled after a validation of exactly what is stored now: anything edited since has no hash to match.
            if (instance.State is not (IntegrationState.Validated or IntegrationState.Disabled) || instance.ValidatedHash is null || instance.ValidatedHash != IntegrationHash.Of(instance))
            {
                return Problem(StatusCodes.Status409Conflict, "Not validated", "Validate the current configuration before enabling it.");
            }
            var now = clock.GetUtcNow();
            instance.State = IntegrationState.Enabled;
            instance.StateReason = null;
            instance.NextRunAt = now;
            instance.RunToken = null;
            instance.ConsecutiveFailures = 0;
            instance.LastFailure = null;
            instance.LastFailureDetail = null;
            Touch(instance, mutation.Actor, now);
            mutation.Audit(ModuleAuditOutcome.Succeeded, id.ToString(), AuditData(instance));
            if (await SaveAsync(mutation, StatusCodes.Status409Conflict, "Integration changed", ct) is { } problem)
            {
                return problem;
            }
            http.Response.Headers.ETag = IntegrationMapping.Etag(instance);
            return Results.Ok(IntegrationMapping.ToDto(instance, plugins, now, detail: true));
        }

        private static async Task<IResult> DisableAsync(Guid tenantId, Guid id, HttpContext http, IntegrationsDbContext db, IIntegrationPluginRegistry plugins, TimeProvider clock,
            CancellationToken ct)
        {
            var (mutation, refused) = await BeginAsync(http, tenantId, "disable", id.ToString(), ct);
            if (mutation is null)
            {
                return refused!;
            }
            var instance = await db.Instances.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == id, ct);
            if (instance is null)
            {
                return NotFound(id);
            }
            var now = clock.GetUtcNow();
            if (instance.State != IntegrationState.Disabled)
            {
                instance.State = IntegrationState.Disabled;
                instance.StateReason = null;
                instance.RunToken = null;
                instance.NextRunAt = null;
                Touch(instance, mutation.Actor, now);
                mutation.Audit(ModuleAuditOutcome.Succeeded, id.ToString(), AuditData(instance));
                if (await SaveAsync(mutation, StatusCodes.Status409Conflict, "Integration changed", ct) is { } problem)
                {
                    return problem;
                }
            }
            http.Response.Headers.ETag = IntegrationMapping.Etag(instance);
            return Results.Ok(IntegrationMapping.ToDto(instance, plugins, now, detail: true));
        }

        // ── Request checks ─────────────────────────────────────────────────────

        private sealed record Normalized(string Name, string Config, string Scope, string Refs, int Interval);

        /// <summary>The request checked against the plugin: the normalized (canonical) values, or the errors keyed by field for the 400.</summary>
        private static (Normalized? Value, Dictionary<string, List<string>> Errors) Normalize(IntegrationInstanceRequest request, IntegrationDescriptor descriptor, IntegrationsOptions options)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            void Add(string key, string message)
            {
                if (!errors.TryGetValue(key, out var list))
                {
                    errors[key] = list = [];
                }
                list.Add(message);
            }

            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 64 || name.Any(char.IsControl))
            {
                Add("name", "is required: 1 to 64 characters, no control characters");
            }
            var empty = JsonDocument.Parse("{}").RootElement;
            var config = request.Config is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } c ? c : empty;
            var scope = request.Scope is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } s ? s : empty;
            foreach (var (key, list) in IntegrationConfigValidator.Validate(descriptor.Config, config, "config", IntegrationConfigValidator.MaxConfigBytes))
            {
                list.ForEach(m => Add(key, m));
            }
            foreach (var (key, list) in IntegrationConfigValidator.Validate(descriptor.Scope, scope, "scope", IntegrationConfigValidator.MaxScopeBytes))
            {
                list.ForEach(m => Add(key, m));
            }

            var refs = request.SecretRefs ?? [];
            foreach (var (secret, reference) in refs)
            {
                if (!descriptor.Secrets.Any(d => d.Name == secret))
                {
                    Add($"secretRefs.{secret}", "is not a secret of this plugin");
                }
                else if (SecretReference.Error(reference, options.SecretEnvPrefix, options.SecretDirectory) is { } why)
                {
                    Add($"secretRefs.{secret}", why);
                }
            }
            foreach (var spec in descriptor.Secrets.Where(d => d.Required && !refs.ContainsKey(d.Name)))
            {
                Add($"secretRefs.{spec.Name}", "is required");
            }

            var interval = request.IntervalSeconds ?? (int)descriptor.DefaultInterval.TotalSeconds;
            if (interval < descriptor.MinInterval.TotalSeconds || interval > TimeSpan.FromHours(24).TotalSeconds)
            {
                Add("intervalSeconds", $"must be between {(int)descriptor.MinInterval.TotalSeconds} and {(int)TimeSpan.FromHours(24).TotalSeconds}");
            }
            return errors.Count > 0
                ? (null, errors)
                : (new Normalized(name!, IntegrationJson.Canonical(config), IntegrationJson.Canonical(scope),
                    IntegrationJson.Canonical(JsonSerializer.SerializeToElement(refs.OrderBy(r => r.Key, StringComparer.Ordinal).ToDictionary(r => r.Key, r => r.Value))), interval), errors);
        }
    }
}
