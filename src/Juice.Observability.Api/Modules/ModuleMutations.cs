using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Juice.Observability.Api.Modules
{
    /// <summary>
    /// <see cref="IModuleMutations"/> over the core's <see cref="Mutation"/>: the audit record, the idempotency record and the role check are the
    /// core's own, in the core's store. The tenant is never taken from the caller: it must be the route tenant the core's tenant filter authorized.
    /// </summary>
    internal sealed class ModuleMutations(PlatformDbContext db) : IModuleMutations
    {
        public IModuleMutation Begin(HttpContext http, Guid tenantId, string operation, string targetType)
        {
            var module = http.GetEndpoint()?.Metadata.GetMetadata<ModuleEndpointMetadata>()?.Module
                ?? throw new InvalidOperationException("A module mutation begins only in an endpoint a module mapped through IModuleEndpointsBuilder.Tenant.");
            if (!operation.StartsWith(module + ".", StringComparison.Ordinal) || operation.Length == module.Length + 1)
            {
                throw new InvalidOperationException($"Operation '{operation}' of module '{module}' must start with '{module}.'.");
            }
            // Present only once the core's tenant filter has let the caller in, for the tenant in the route.
            if (!http.Items.ContainsKey(TelemetryEndpoints.TenantKeyItem)
                || !Guid.TryParse(http.GetRouteValue("tenantId") as string, out var routeTenant) || routeTenant != tenantId)
            {
                throw new InvalidOperationException("A module mutation is for the tenant the core authorized for this route.");
            }
            return new ModuleMutation(new Mutation(http, db, tenantId, operation, targetType), db);
        }

        private sealed class ModuleMutation(Mutation mutation, PlatformDbContext db) : IModuleMutation
        {
            private IDbContextTransaction? _transaction;

            public string Actor => mutation.Actor;

            public async Task BeginTransactionAsync(CancellationToken ct)
            {
                if (_transaction is not null)
                {
                    throw new InvalidOperationException("A transaction is already open.");
                }
                _transaction = await db.Database.BeginTransactionAsync(ct);
            }

            public async Task<IResult?> RequireAsync(string? target, CancellationToken ct, params string[] roles)
            {
                // Only the core's roles: a module can't grant a caller a role the identity provider never gave it.
                if (roles.Length == 0 || roles.Any(r => !CoreRoles.All.Contains(r)))
                {
                    throw new ArgumentException("Roles are a non-empty subset of CoreRoles.All.", nameof(roles));
                }
                var denied = await mutation.RequireAsync(target, ct, roles);
                if (denied is not null && _transaction is { } open)
                {
                    // The denial's audit record was saved inside the open transaction: commit it, or the refusal would roll it back.
                    _transaction = null;
                    await using (open)
                    {
                        await open.CommitAsync(ct);
                    }
                }
                return denied;
            }

            public void Audit(ModuleAuditOutcome outcome, string? target, IReadOnlyDictionary<string, string>? metadata = null)
                => mutation.Audit(outcome switch
                {
                    ModuleAuditOutcome.Succeeded => AuditOutcome.Succeeded,
                    ModuleAuditOutcome.Denied => AuditOutcome.Denied,
                    ModuleAuditOutcome.Failed => AuditOutcome.Failed,
                    _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
                }, target, metadata is null ? null : new Dictionary<string, string>(metadata));

            public Task<IResult?> ReplayAsync(string route, object request, CancellationToken ct) => mutation.ReplayAsync(route, request, ct);

            public IResult Remember<T>(int status, T response, DateTimeOffset now) => mutation.Remember(status, response, now);

            public async Task SaveAsync(CancellationToken ct)
            {
                await db.SaveChangesAsync(ct);
                if (_transaction is { } open)
                {
                    _transaction = null;
                    await using (open)
                    {
                        await open.CommitAsync(ct);
                    }
                }
            }
        }
    }
}
