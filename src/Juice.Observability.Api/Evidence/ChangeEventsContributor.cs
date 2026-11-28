using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Extensibility;
using Microsoft.EntityFrameworkCore;

namespace Juice.Observability.Api.Evidence
{
    /// <summary>
    /// <c>core.changes</c>: the changes of an alert's related items, the same query as <c>/alerts/{id}/related</c> (<see cref="ChangeEndpoints.RelatedForAsync"/>)
    /// with its health items left out. The alert page keeps its own block for them, so this section isn't drawn there.
    /// </summary>
    internal sealed class ChangeEventsContributor(PlatformDbContext db) : IEvidenceContributor
    {
        public string Key => "core.changes";

        public EvidenceTarget Target => EvidenceTarget.Alert;

        public async Task<EvidenceSection?> ContributeAsync(EvidenceContext context, CancellationToken ct)
        {
            var alert = await db.Alerts.AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == context.TenantId && a.Id == context.SubjectId, ct);
            if (alert is null)
            {
                return null;
            }
            var related = await ChangeEndpoints.RelatedForAsync(context.TenantId, alert, db, ct);
            var items = related.Items.Where(i => i.Type == "change").Select(i => new EvidenceItem(i.Title, i.Detail, i.At)).ToList();
            return items.Count == 0 ? null : new EvidenceSection("Changes around this alert", items, related.Note);
        }
    }
}
