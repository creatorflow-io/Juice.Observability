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

        /// <summary>
        /// Waits for commands. With <c>trustedKeys</c>, the command keys the agent trusts, the platform can announce its current one; without, no
        /// header is sent, as an agent from before rotation did.
        /// </summary>
        public async Task<PollResponse> PollAsync(int waitSeconds, string version, string hostname, CancellationToken cancellationToken, IEnumerable<string>? trustedKeys = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{AgentRoutes.Commands}?wait={waitSeconds}");
            request.Headers.Add(AgentRoutes.VersionHeader, version);
            request.Headers.Add(AgentRoutes.HostnameHeader, hostname);
            if (trustedKeys is not null)
            {
                request.Headers.Add(AgentRoutes.KeysHeader, string.Join(',', trustedKeys));
            }
            return await ReadAsync<PollResponse>(await _http.SendAsync(request, cancellationToken), cancellationToken);
        }

        /// <summary>A package and its release signature. Larger than <see cref="AgentPackages.MaxBytes"/> is refused before or while it's read.</summary>
        public async Task<(byte[] Package, byte[] Signature)> DownloadAsync(string version, CancellationToken cancellationToken)
        {
            using var response = await _http.GetAsync(AgentRoutes.Package(version), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new UpdateFailedException("package_not_found");
            }
            await EnsureAsync(response, cancellationToken);
            if (!response.Headers.TryGetValues(AgentRoutes.PackageSignatureHeader, out var values) || !TryBase64(values.First(), out var signature))
            {
                throw new UpdateFailedException("package_unsigned");
            }
            if (response.Content.Headers.ContentLength > AgentPackages.MaxBytes)
            {
                throw new UpdateFailedException("package_too_large");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > AgentPackages.MaxBytes)
                {
                    throw new UpdateFailedException("package_too_large");
                }
                buffer.Write(chunk, 0, read);
            }
            return (buffer.ToArray(), signature);
        }

        private static bool TryBase64(string value, out byte[] bytes)
        {
            try
            {
                bytes = Convert.FromBase64String(value);
                return bytes.Length is > 0 and < 1024;
            }
            catch (FormatException)
            {
                bytes = [];
                return false;
            }
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

        /// <summary>
        /// Reports the actions this agent can run. False when the platform has nowhere to take it (an older version answers 404); a report it refuses
        /// as invalid throws <see cref="ResultRefusedException"/>, since sending it again can't help.
        /// </summary>
        public async Task<bool> ReportActionsAsync(ActionsReport report, CancellationToken cancellationToken)
        {
            using var response = await _http.PostAsJsonAsync(AgentRoutes.Actions, report, CommandSignature.Json, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new ResultRefusedException(detail[..Math.Min(detail.Length, 500)]);
            }
            await EnsureAsync(response, cancellationToken);
            return true;
        }

        private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            using (response)
            {
                await EnsureAsync(response, cancellationToken);
                return (await response.Content.ReadFromJsonAsync<T>(CommandSignature.Tolerant, cancellationToken))!;
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
