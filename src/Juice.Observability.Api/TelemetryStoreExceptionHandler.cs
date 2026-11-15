using Juice.Observability.Discovery;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Juice.Observability.Api
{
    /// <summary>
    /// A telemetry query refused for load, or ClickHouse out of reach, is a 503 with <c>Retry-After</c>, not a 500 (Phase 11 R11): the Console
    /// shows the problem's title and detail, "Busy, try again", and the scale harness counts it as a failed search.
    /// </summary>
    public sealed class TelemetryStoreExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
    {
        public const int BusyRetrySeconds = 5;
        public const int UnavailableRetrySeconds = 15;

        public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken cancellationToken)
        {
            var (title, detail, retry) = exception switch
            {
                TelemetryBusyException => ("Busy, try again",
                    "The telemetry store is answering as many queries as it allows. Try again in a few seconds, or narrow the time range.", BusyRetrySeconds),
                TelemetryUnavailableException => ("Telemetry store unavailable",
                    "The telemetry store can't be reached. Try again shortly; the platform's status shows when it's back.", UnavailableRetrySeconds),
                _ => (null, null, 0),
            };
            if (title is null)
            {
                return false;
            }
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            http.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return await problems.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = http,
                Exception = exception,
                ProblemDetails = new ProblemDetails { Status = StatusCodes.Status503ServiceUnavailable, Title = title, Detail = detail },
            });
        }
    }
}
