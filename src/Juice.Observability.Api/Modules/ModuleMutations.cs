using Juice.Observability.Api.Endpoints;
using Juice.Observability.Api.Modules.ModuleData;
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
    internal sealed class ModuleMutations(PlatformDbContext db, IServiceProvider services) : IModuleMutations
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
            return new ModuleMutation(new Mutation(http, db, tenantId, operation, targetType), db, services.GetService<ModuleDataSession>(), http);
        }

        private sealed class ModuleMutation(Mutation mutation, PlatformDbContext db, ModuleDataSession? modules, HttpContext http) : IModuleMutation
        {
            private const string Begun = "module_begin";

            private IDbContextTransaction? _transaction;

            public string Actor => mutation.Actor;

            public async Task BeginTransactionAsync(CancellationToken ct)
            {
                if (_transaction is not null)
                {
                    throw new InvalidOperationException("A transaction is already open.");
                }
                _transaction = await db.Database.BeginTransactionAsync(ct);
                // What the module saved after this point is undone, and the transaction goes on, if the caller turns out not to be allowed (RequireAsync).
                await _transaction.CreateSavepointAsync(Begun, ct);
                // The module's contexts, those created already and any created later, run in it: one commit for the module's rows, the audit and the idempotency record.
                if (modules is not null)
                {
                    await modules.EnlistAsync(_transaction.GetDbTransaction(), ct);
                }
            }

            private async Task EndAsync(bool commit, CancellationToken ct)
            {
                var open = _transaction;
                _transaction = null;
                if (open is null)
                {
                    return;
                }
                await using (open)
                {
                    if (commit)
                    {
                        await open.CommitAsync(ct);
                    }
                    else
                    {
                        await open.RollbackAsync(ct);
                    }
                }
                if (modules is not null)
                {
                    // A context left holding a finished transaction couldn't run another command.
                    await modules.ReleaseAsync(ct);
                }
            }

            public async Task<IResult?> RequireAsync(string? target, CancellationToken ct, params string[] roles)
            {
                // Only the core's roles: a module can't grant a caller a role the identity provider never gave it.
                if (roles.Length == 0 || roles.Any(r => !CoreRoles.All.Contains(r)))
                {
                    throw new ArgumentException("Roles are a non-empty subset of CoreRoles.All.", nameof(roles));
                }
                if (_transaction is not null && !roles.Any(http.User.IsInRole))
                {
                    // Denied after the module began writing: nothing of it commits with the denial's audit record.
                    await _transaction.RollbackToSavepointAsync(Begun, ct);
                }
                var denied = await mutation.RequireAsync(target, ct, roles);
                if (denied is not null)
                {
                    // The denial's audit record was saved inside the open transaction: commit it, or the refusal would roll it back.
                    // Nothing of the module was saved: the request is refused before it writes.
                    await EndAsync(commit: true, ct);
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

            /// <summary>
            /// Every module context of the request, then the core's (the audit and idempotency records), in one transaction: the one opened by
            /// <see cref="BeginTransactionAsync"/>, or one opened here. A failure, the audit's included, rolls back the module's rows too.
            /// </summary>
            public async Task SaveAsync(CancellationToken ct)
            {
                // Without one of its own, joins a transaction already open on the core's connection and leaves its commit to whoever began it.
                var joined = _transaction is not null || db.Database.CurrentTransaction is not null;
                if (!joined && modules is not null)
                {
                    await BeginTransactionAsync(ct);
                }
                try
                {
                    if (modules is not null)
                    {
                        await modules.SaveAsync(ct);
                    }
                    await db.SaveChangesAsync(ct);
                }
                catch
                {
                    try
                    {
                        await EndAsync(commit: false, CancellationToken.None);
                    }
                    catch (Exception)
                    {
                        // The save's own failure is the one to report; a connection that can't roll back has dropped the transaction anyway.
                    }
                    throw;
                }
                await EndAsync(commit: true, ct);
            }
        }
    }
}
