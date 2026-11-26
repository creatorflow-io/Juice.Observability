using Juice.Notifications;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Juice.Observability.Api
{
    /// <summary>
    /// The notification pipeline's <c>concurrent_change</c> reaching a request (the alert's correlation changed under the call, and the inline
    /// retries didn't get past it) is a 409 <c>concurrent_change</c>, not a 500: the change rolled back, and trying again goes against the new state.
    /// The adapter leaves it to heal on a person's or an agent's call, so this is the last guard, not the usual answer.
    /// </summary>
    public sealed class NotificationsConflictExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
    {
        public const string Code = "concurrent_change";

        public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken cancellationToken)
        {
            if (Find(exception) is not { } refused)
            {
                return false;
            }
            const string title = "Changed meanwhile, try again";
            var detail = $"The alert's notifications changed while this was saved ({refused.Message}). Nothing was changed: try again.";
            http.Response.StatusCode = StatusCodes.Status409Conflict;
            var problem = new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = title, Detail = detail, Type = Code };
            problem.Extensions["code"] = Code;
            if (!await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, Exception = exception, ProblemDetails = problem }))
            {
                http.Response.ContentType = "text/plain; charset=utf-8";
                await http.Response.WriteAsync($"{title}. {detail}", cancellationToken);
            }
            return true;
        }

        /// <summary>The refusal, also when something wrapped it.</summary>
        internal static NotificationRefusedException? Find(Exception? exception)
        {
            for (; exception is not null; exception = exception.InnerException)
            {
                if (exception is NotificationRefusedException { Code: ErrorCodes.ConcurrentChange } refused)
                {
                    return refused;
                }
            }
            return null;
        }
    }
}
