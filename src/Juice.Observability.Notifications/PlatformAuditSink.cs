using System.Text.Json;
using Juice.Notifications;
using Juice.Notifications.Data;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// The pipeline's changes to its setup as the platform's audit records (phase-5-plan I62): one <see cref="AuditRecord"/> per changed row,
    /// written in the transaction that makes the change, so it commits or rolls back with it. That is the platform's own transaction when the change
    /// comes through the client, and the pipeline's when it makes the change itself (provisioning at start).
    /// <para>The record names the table, the row's key and the fields that changed, never their values: a pipeline row may hold a secret
    /// (an integration's), and the platform's audit is read by every operator.</para>
    /// </summary>
    internal sealed class PlatformAuditSink(NotificationsDbContext db, IOptions<PlatformOptions> platform, TimeProvider clock) : INotificationsAuditSink
    {
        /// <summary>Who changed it when the pipeline doesn't say (provisioning at start names itself).</summary>
        public const string Actor = "notifications";

        public async ValueTask RecordAsync(NotificationsDataChange change, CancellationToken cancellationToken)
        {
            var operation = Bounded($"notifications.{change.Table}.{change.Change.ToLowerInvariant()}", 100);
            var key = Bounded(string.Join(",", change.Keys.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}")), 256);
            var fields = change.Before.Keys.Union(change.After.Keys).Where(f => !Equals(Text(change.Before, f), Text(change.After, f)))
                .Order(StringComparer.Ordinal).ToList();
            var metadata = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["source"] = "notifications", ["table"] = change.Table, ["change"] = change.Change, ["fields"] = string.Join(",", fields),
            });
            var actor = Bounded(change.User is { Length: > 0 } user ? user : Actor, 256);
            var correlation = Juice.Messaging.MessageContext.IsInitialized ? Bounded(Juice.Messaging.MessageContext.Current.CorrelationId, 128) : null;
            // The platform tenant by its key (its Id in the pipeline, PR #4): a row of the platform's, so its own id, not the key.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO public.audit_records (id, tenant_id, actor, operation, target_type, target_id, outcome, correlation_id, occurred_at, metadata)
                SELECT {Guid.CreateVersion7()}, t.id, {actor}, {operation}, {Bounded(change.Table, 100)}, {key}, {nameof(AuditOutcome.Succeeded)}, {correlation},
                       {clock.GetUtcNow()}, CAST({metadata} AS jsonb)
                FROM public.platform_tenants t WHERE t.key = {platform.Value.TenantKey}
                """, cancellationToken);
        }

        private static string? Text(IReadOnlyDictionary<string, object?> values, string field)
            => values.TryGetValue(field, out var value) ? value switch { null => null, JsonElement json => json.GetRawText(), _ => value.ToString() } : null;

        private static string? Bounded(string? value, int max) => value is null ? null : value.Length > max ? value[..max] : value;
    }
}
