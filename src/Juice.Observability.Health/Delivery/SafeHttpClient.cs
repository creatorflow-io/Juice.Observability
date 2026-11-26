using System.Net;
using System.Net.Sockets;

namespace Juice.Observability.Health.Delivery
{
    /// <summary>
    /// An HTTP client that only reaches destinations the policy allows (requirements §8, the SSRF defence; I72 moved it here from the webhooks so
    /// the dead-man alarm, which must not depend on the notification pipeline, sends through it too): no redirects, no proxy, no cookies, a connect
    /// timeout, and a connect callback that resolves the host, checks every address against <see cref="DestinationPolicy"/> and the allowlist, and
    /// connects only to a checked address. It runs for every new connection, so DNS is revalidated each time.
    /// </summary>
    public static class SafeHttpClient
    {
        /// <param name="connectTimeout">How long a connection may take.</param>
        /// <param name="allowHttpToAllowlisted">Plain <c>http</c> to an allowlisted destination (never to a public one).</param>
        /// <param name="allowlist">The allowlist as it is now, read for every new connection.</param>
        public static HttpClient Create(TimeSpan connectTimeout, bool allowHttpToAllowlisted,
            Func<CancellationToken, ValueTask<IReadOnlyList<AllowedDestination>>> allowlist)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                ConnectTimeout = connectTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(1),
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var host = context.DnsEndPoint.Host;
                    var port = context.DnsEndPoint.Port;
                    IReadOnlyList<IPAddress> addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                        ? [literal]
                        : await Dns.GetHostAddressesAsync(host, cancellationToken);
                    var allowed = await allowlist(cancellationToken);
                    var scheme = context.InitialRequestMessage.RequestUri?.Scheme ?? "";
                    if (DestinationPolicy.Check(scheme, host, port, addresses, allowed, allowHttpToAllowlisted) is { } reason)
                    {
                        throw new DestinationBlockedException(reason);
                    }
                    var socket = await DestinationPolicy.ConnectAsync(addresses, port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                },
            };
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }
}
