using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Juice.Observability.Agent.Contracts;

namespace Juice.Observability.Agent
{
    /// <summary>The platform refused this agent's certificate: revoked, expired or unknown. Only a new enrollment helps.</summary>
    public sealed class AgentRefusedException(string message) : Exception(message);

    /// <summary>The platform won't take this result, and never will: it isn't for a command of this agent's, or its body is refused.</summary>
    public sealed class ResultRefusedException(string message) : Exception(message);

    /// <summary>
    /// Calls to the platform's agent listener. HTTPS only, the server certificate checked against <see cref="AgentOptions.PlatformCaFile"/> and
    /// nothing else, no proxy-supplied credentials, no redirects. With a client certificate for everything after enrollment.
    /// </summary>
    public sealed class PlatformClient : IDisposable
    {
        private readonly HttpClient _http;

        public PlatformClient(AgentOptions options, X509Certificate2? clientCertificate)
        {
            var trusted = X509CertificateLoader.LoadCertificateFromFile(options.PlatformCaFile);
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                SslOptions = new SslClientAuthenticationOptions
                {
                    ClientCertificates = clientCertificate is null ? null : [clientCertificate],
                    RemoteCertificateValidationCallback = (_, certificate, chain, errors) => IsPlatform(certificate, chain, errors, trusted),
                },
            };
            _http = new HttpClient(handler) { BaseAddress = new Uri(options.PlatformUrl), Timeout = TimeSpan.FromSeconds(options.PollWaitSeconds + 15) };
        }

        /// <summary>
        /// Signed by the configured CA, for the name in the URL; the machine's own trust store plays no part. Intermediates the server sent are used
        /// to build the chain, never trusted for themselves: the chain must still end at the configured CA.
        /// </summary>
        internal static bool IsPlatform(X509Certificate? certificate, X509Chain? sent, SslPolicyErrors errors, X509Certificate2 trusted)
        {
            if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            {
                return false;
            }
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trusted);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (sent is not null)
            {
                // SslStream puts the certificates the server sent here.
                chain.ChainPolicy.ExtraStore.AddRange(sent.ChainPolicy.ExtraStore);
            }
            return chain.Build(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
        }

        public async Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken cancellationToken)
            => await ReadAsync<EnrollResponse>(await _http.PostAsJsonAsync(AgentRoutes.Enroll, request, CommandSignature.Json, cancellationToken), cancellationToken);

        public async Task<RenewResponse> RenewAsync(RenewRequest request, CancellationToken cancellationToken)
            => await ReadAsync<RenewResponse>(await _http.PostAsJsonAsync(AgentRoutes.Certificate, request, CommandSignature.Json, cancellationToken), cancellationToken);

        public async Task<PollResponse> PollAsync(int waitSeconds, string version, string hostname, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{AgentRoutes.Commands}?wait={waitSeconds}");
            request.Headers.Add(AgentRoutes.VersionHeader, version);
            request.Headers.Add(AgentRoutes.HostnameHeader, hostname);
            return await ReadAsync<PollResponse>(await _http.SendAsync(request, cancellationToken), cancellationToken);
        }

        public async Task ReportAsync(CommandResult result, CancellationToken cancellationToken)
        {
            using var response = await _http.PostAsJsonAsync(AgentRoutes.Result(result.CommandId), result, CommandSignature.Json, cancellationToken);
            if ((int)response.StatusCode is >= 400 and < 500 and not (401 or 403 or 408 or 429))
            {
                // Not a command of this agent's, or a body the platform won't take: sending it again can't help.
                throw new ResultRefusedException($"{(int)response.StatusCode}");
            }
            await EnsureAsync(response, cancellationToken);
        }

        private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            using (response)
            {
                await EnsureAsync(response, cancellationToken);
                return (await response.Content.ReadFromJsonAsync<T>(CommandSignature.Json, cancellationToken))!;
            }
        }

        private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new AgentRefusedException($"{(int)response.StatusCode}: {detail[..Math.Min(detail.Length, 300)]}");
            }
            throw new HttpRequestException($"{(int)response.StatusCode}: {detail[..Math.Min(detail.Length, 300)]}", null, response.StatusCode);
        }

        public void Dispose() => _http.Dispose();
    }
}
