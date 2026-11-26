using System.Text.Json;
using Juice.MediatR;
using Juice.Notifications;
using Juice.Notifications.Configuration;

namespace Juice.Observability.Notifications.Migration
{
    /// <summary>
    /// Sends the setup's commands, one at a time, saving each before the next (a route's checks read the targets saved before it): the
    /// migration's own transaction or the host's (<see cref="Juice.Notifications.Embedded.INotificationsHostScope"/>, the alias).
    /// </summary>
    internal interface ISetupSender
    {
        /// <returns>What notifications refused, or null when it took the command.</returns>
        Task<MappingProblem?> SendAsync<T>(IRequest<Outcome<T>> command, CancellationToken ct);
    }

    /// <summary>The commands that make a legacy endpoint's notifications rows, as the migration and the alias both write them, as the alias's.</summary>
    internal static class EndpointWrites
    {
        /// <summary>
        /// The integration (with <paramref name="id"/> when it is created), its targets, the route, then the alias's targets the setup no longer
        /// has (<paramref name="surplus"/>), after the route stops naming them. Stops at the first refusal.
        /// </summary>
        public static async Task<MappingProblem?> PutAsync(ISetupSender sender, Guid tenant, EndpointSetup setup, IReadOnlyList<string> surplus, Guid? id,
            CancellationToken ct)
        {
            var i = setup.Integration;
            if (await sender.SendAsync(new PutIntegration(tenant, i.Key, i.Channel, i.Kind, JsonElement.Parse(i.Settings), i.SecretRef, i.Priority, i.Enabled)
                { ManagedBy = AliasVocabulary.WebhooksAlias, Id = id }, ct) is { } refused)
            {
                return refused;
            }
            foreach (var t in setup.Targets)
            {
                if (await sender.SendAsync(new PutTarget(tenant, t.IntegrationKey, t.Key, t.Address, t.AddressRef, t.Enabled) { ManagedBy = AliasVocabulary.WebhooksAlias }, ct)
                    is { } target)
                {
                    return target;
                }
            }
            var r = setup.Route;
            if (await sender.SendAsync(new PutRoute(tenant, r.Key, JsonElement.Parse(r.Match), r.Targets, r.Enabled, r.DigestSeconds)
                { ManagedBy = AliasVocabulary.WebhooksAlias }, ct) is { } route)
            {
                return route;
            }
            foreach (var key in surplus)
            {
                if (await sender.SendAsync(new DeleteTarget(tenant, i.Key, key) { ManagedBy = AliasVocabulary.WebhooksAlias }, ct) is { } deleted)
                {
                    return deleted;
                }
            }
            return null;
        }

        public static MappingProblem Refused(string? code, string? message, IReadOnlyList<FieldError>? errors)
            => new(code ?? "refused", "Notifications refused the write: " + message
                + (errors is { Count: > 0 } ? " " + string.Join("; ", errors.Select(e => $"{e.Pointer}: {e.Message}")) : ""));

        /// <summary>The refusal an outcome is, or null when it succeeded.</summary>
        public static MappingProblem? Problem<T>(Outcome<T> outcome)
            => outcome.Kind is OutcomeKind.Ok or OutcomeKind.Created ? null : Refused(outcome.Code, outcome.Message, outcome.Errors);
    }
}
