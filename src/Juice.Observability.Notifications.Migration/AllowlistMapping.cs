using Juice.Notifications.Configuration;
using Juice.Notifications.Providers;

namespace Juice.Observability.Notifications.Migration
{
    /// <summary>
    /// A legacy allowlist entry as a <c>destination_allowlist</c> row (phase-5-plan I66), and back. Both hold a host name or a CIDR with its
    /// ports, no ports meaning 443 only. The destination is written as notifications stores it (<see cref="ConfigurationRules.Destination"/>:
    /// a lowercase ASCII host, a canonical network), and the ports in order: an entry already in that form maps back to itself.
    /// </summary>
    public static class AllowlistMapping
    {
        public static Mapped<AllowlistDefinition> Map(LegacyAllowlistEntry entry)
        {
            var destination = ConfigurationRules.Destination(entry.Destination.Trim().ToLowerInvariant());
            var ports = entry.Ports.Distinct().Order().ToList();
            if (destination is null || ports.Count > 20 || ports.Any(p => p is < 1 or > 65535))
            {
                return Mapped<AllowlistDefinition>.Refused(new MappingProblem(ProblemCodes.Destination,
                    $"'{entry.Destination}' isn't a host name or a network notifications takes, with up to 20 ports of 1–65535."));
            }
            if (DestinationPolicy.ValidateEntry(new AllowedDestination(destination, ports)) is { } refused)
            {
                return Mapped<AllowlistDefinition>.Refused(new MappingProblem(ProblemCodes.Destination, refused));
            }
            return Mapped<AllowlistDefinition>.Ok(new AllowlistDefinition(destination, ports));
        }

        public static LegacyAllowlistEntry Unmap(AllowlistDefinition row) => new(row.Destination, row.Ports.Order().ToList());
    }
}
