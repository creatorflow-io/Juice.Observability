using System.Net;
using System.Net.Sockets;

namespace Juice.Observability.Webhooks
{
    public enum DestinationClass
    {
        /// <summary>Reachable without an allowlist entry.</summary>
        Public,
        /// <summary>Private or internal: only with an allowlist entry.</summary>
        Private,
        /// <summary>Never reachable, allowlisted or not: loopback, link-local (cloud metadata), unspecified, multicast, reserved.</summary>
        Blocked,
    }

    /// <summary>An allowlist entry: a CIDR, or an exact host name, with its ports.</summary>
    public sealed record AllowedDestination(string Destination, IReadOnlyList<int> Ports)
    {
        public bool IsCidr => Destination.Contains('/');

        public bool Covers(string host, IPAddress address, int port)
        {
            if (!(Ports.Count == 0 ? port == 443 : Ports.Contains(port)))
            {
                return false;
            }
            return IsCidr
                ? IPNetwork.TryParse(Destination, out var network) && network.Contains(DestinationPolicy.Normalize(address))
                : string.Equals(Destination, host, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// SSRF rules for webhook destinations (requirements §8). Every address a host resolves to is checked on every new
    /// connection; one blocked address rejects the destination, so DNS that mixes public and internal answers cannot
    /// be used to reach inside. Loopback, link-local and cloud metadata stay blocked even when an allowlist entry
    /// covers them.
    /// </summary>
    public static class DestinationPolicy
    {
        private static readonly IPNetwork[] Blocked =
        [
            IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"),
            IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("198.51.100.0/24"), IPNetwork.Parse("203.0.113.0/24"),
            IPNetwork.Parse("224.0.0.0/4"), IPNetwork.Parse("240.0.0.0/4"),
            IPNetwork.Parse("::/128"), IPNetwork.Parse("::1/128"), IPNetwork.Parse("fe80::/10"), IPNetwork.Parse("ff00::/8"),
            IPNetwork.Parse("fd00:ec2::254/128"), IPNetwork.Parse("2001:db8::/32"),
        ];

        private static readonly IPNetwork[] Private =
        [
            IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("192.168.0.0/16"),
            IPNetwork.Parse("100.64.0.0/10"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("192.0.0.0/24"),
            IPNetwork.Parse("fc00::/7"), IPNetwork.Parse("64:ff9b:1::/48"),
        ];

        /// <summary>IPv4-mapped IPv6 (<c>::ffff:127.0.0.1</c>) is judged as the IPv4 address it carries.</summary>
        public static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        public static DestinationClass Classify(IPAddress address)
        {
            var ip = Normalize(address);
            if (ip.Equals(IPAddress.Broadcast) || Blocked.Any(n => n.Contains(ip)))
            {
                return DestinationClass.Blocked;
            }
            return Private.Any(n => n.Contains(ip)) ? DestinationClass.Private : DestinationClass.Public;
        }

        /// <summary>Null when the connection may proceed; otherwise why it may not.</summary>
        public static string? Check(string scheme, string host, int port, IReadOnlyList<IPAddress> addresses,
            IReadOnlyList<AllowedDestination> allowlist, bool allowHttpToAllowlisted)
        {
            if (addresses.Count == 0)
            {
                return "no_address";
            }
            foreach (var address in addresses)
            {
                var entry = allowlist.FirstOrDefault(a => a.Covers(host, address, port));
                switch (Classify(address))
                {
                    case DestinationClass.Blocked:
                        return $"blocked_address:{Normalize(address)}";
                    case DestinationClass.Private when entry is null:
                        return $"private_address_not_allowlisted:{Normalize(address)}";
                }
                if (scheme != Uri.UriSchemeHttps && !(allowHttpToAllowlisted && entry is not null))
                {
                    return "https_required";
                }
            }
            return null;
        }

        /// <summary>Configuration check: an allowlist entry that only covers never-reachable addresses is a mistake.</summary>
        public static string? ValidateEntry(AllowedDestination entry)
        {
            if (entry.Ports.Any(p => p is < 1 or > 65535))
            {
                return $"allowlist '{entry.Destination}': ports are 1 to 65535";
            }
            if (!entry.IsCidr)
            {
                return Uri.CheckHostName(entry.Destination) == UriHostNameType.Dns ? null : $"allowlist '{entry.Destination}': not a CIDR or host name";
            }
            if (!IPNetwork.TryParse(entry.Destination, out var network))
            {
                return $"allowlist '{entry.Destination}': not a valid CIDR";
            }
            return Classify(network.BaseAddress) == DestinationClass.Blocked
                ? $"allowlist '{entry.Destination}': loopback, link-local, metadata and reserved addresses are never reachable"
                : null;
        }

        /// <summary>A socket to the first address of <paramref name="addresses"/> that accepts, after they all passed <see cref="Check"/>.</summary>
        internal static async Task<Socket> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken)
        {
            Exception? last = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
                    return socket;
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                {
                    socket.Dispose();
                    last = ex;
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
            throw last ?? new SocketException((int)SocketError.HostNotFound);
        }
    }

    /// <summary>The destination failed <see cref="DestinationPolicy.Check"/>; never retried.</summary>
    public sealed class DestinationBlockedException(string reason) : Exception($"Webhook destination blocked: {reason}")
    {
        public string Reason { get; } = reason;
    }
}
