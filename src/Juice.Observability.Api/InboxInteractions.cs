using Microsoft.EntityFrameworkCore;
using Juice.Notifications.Inbox;
using Juice.MediatR;
using Juice.Observability.Agents;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Juice.Observability.Notifications;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Api
{
    /// <summary>
    /// What the bell's Approve, Reject and Acknowledge do (M56): the same methods the Approvals and Alert pages call
    /// (<see cref="ActionRequestEndpoints.ApproveCoreAsync"/>, <see cref="ActionRequestEndpoints.RejectCoreAsync"/> and
    /// <see cref="AlertEndpoints.AcknowledgeCoreAsync"/>), so the role, the tenant, the second person, the expiry, the row lock, the evidence and the
    /// audit are the page's own, with <c>via=inbox</c> added to each audit record.
    /// <para>
    /// Authority is the <b>current token's</b> (<c>IsInRole</c>, and the tenant claim judged by <see cref="TenantScope"/> against an Active tenant row):
    /// never the roles recorded when the item was made, and never the item's existence. A demoted admin's old item is refused (403) like any other
    /// request. Nothing here logs a token or a header.
    /// </para>
    /// </summary>
    internal sealed class ObservabilityInboxInteractions(PlatformDbContext db, IServiceProvider services, IOptions<PlatformOptions> platform,
        IOptions<ActionApprovalOptions> approval, TimeProvider clock, IMediator mediator) : IInboxInteractionHandler
    {
        /// <summary>What the bell records when it rejects: the page requires a comment, and the item has no field for one.</summary>
        internal const string RejectComment = "Rejected from the inbox.";

        /// <summary>The marker of the audit records the bell writes.</summary>
        internal const string Via = "inbox";

        private const string ApprovalPrefix = "approval:";
        private const string AlertPrefix = "alert:";

        public IReadOnlyCollection<string> Types { get; } = [ObservabilityTypes.ActionApproval, ObservabilityTypes.AlertFiring];

        public async Task<IResult?> DecideAsync(InboxInteraction interaction, HttpContext http, CancellationToken ct)
        {
            // Named as the other entry points name theirs (a fixed set: never a value of the request).
            PlatformCaller.Set(interaction.Kind == InteractionKinds.Acknowledged ? "inbox:/acknowledge" : "inbox:/actions", ct);
            using var context = PlatformMessageContext.Begin("inbox", CorrelationId.Get(http));

            // The interaction's tenant is the pipeline's (this deployment's key as an Id), and the platform's rows are under the tenant row's own id:
            // the one is this deployment's row, and the token's claim is judged against that Active row as every tenant route judges it.
            // Anything else is no tenant of this deployment: Guid.Empty is no row, and gets the same 403.
            var tenant = interaction.TenantId == PlatformOptions.TenantId(platform.Value.TenantKey)
                ? await db.Tenants.AsNoTracking().Where(t => t.Key == platform.Value.TenantKey).Select(t => t.Id).SingleOrDefaultAsync(ct) : Guid.Empty;
            if (await TenantScope.AuthorizeAsync(http, tenant) is { } denied)
            {
                return denied;
            }
            if (!TryParse(interaction.TypeKey, interaction.CorrelationKey, out var id))
            {
                return Unhandled();
            }
            IResult result;
            if (interaction.TypeKey == ObservabilityTypes.ActionApproval && interaction.Kind == InteractionKinds.Action
                && interaction.ActionId == ObservabilityTypes.ApproveAction)
            {
                result = await ActionRequestEndpoints.ApproveCoreAsync(tenant, id, null, Via, http, db, services, platform, approval, clock, ct);
            }
            else if (interaction.TypeKey == ObservabilityTypes.ActionApproval && interaction.Kind == InteractionKinds.Action
                && interaction.ActionId == ObservabilityTypes.RejectAction)
            {
                result = await ActionRequestEndpoints.RejectCoreAsync(tenant, id, RejectComment, Via, http, db, services, clock, ct);
            }
            else if (interaction.TypeKey == ObservabilityTypes.AlertFiring && interaction.Kind == InteractionKinds.Acknowledged)
            {
                result = await AlertEndpoints.AcknowledgeCoreAsync(tenant, id, null, Via, http, db, mediator, ct);
            }
            else
            {
                return Unhandled();
            }
            // A success is accepted (the inbox records it); anything else goes back to the person as the page would have answered it.
            return result is IStatusCodeHttpResult { StatusCode: null or (>= 200 and < 300) } ? null : result;
        }

        /// <summary>As the inbox answers a type no handler decides (<c>interaction_unhandled</c>).</summary>
        private static IResult Unhandled() => Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Nothing here decides this item.",
            extensions: new Dictionary<string, object?> { ["code"] = Juice.Notifications.ErrorCodes.InteractionUnhandled });

        /// <summary>
        /// The id in the item's correlation key, only in the one form the producer writes: the prefix of the item's type, then a lowercase
        /// <c>D</c>-format guid and nothing else (no case, space, line break or other format).
        /// </summary>
        internal static bool TryParse(string typeKey, string? correlationKey, out Guid id)
        {
            id = Guid.Empty;
            var prefix = typeKey == ObservabilityTypes.ActionApproval ? ApprovalPrefix : typeKey == ObservabilityTypes.AlertFiring ? AlertPrefix : null;
            if (prefix is null || correlationKey is null || !correlationKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
            var text = correlationKey[prefix.Length..];
            return text.Length == 36 && Guid.TryParseExact(text, "D", out id) && string.Equals(text, id.ToString("D"), StringComparison.Ordinal);
        }
    }
}
