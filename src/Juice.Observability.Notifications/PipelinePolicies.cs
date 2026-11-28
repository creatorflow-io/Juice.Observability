using Juice.Notifications.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// Reads the embedded tenant's escalation policies in the notification pipeline (a read in the same process, never another tenant's). The
    /// platform keeps which policy an alert escalates by; the pipeline keeps what it does, under the same key.
    /// </summary>
    public sealed class PipelinePolicies(NotificationsDbContext db, IOptions<PlatformOptions> platform)
    {
        /// <summary>Whether the embedded tenant has a pipeline escalation policy with this key.</summary>
        public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
        {
            var tenant = PlatformOptions.TenantId(platform.Value.TenantKey);
            return await db.EscalationPolicies.AsNoTracking().AnyAsync(p => p.TenantId == tenant && p.Key == key, cancellationToken);
        }
    }
}
