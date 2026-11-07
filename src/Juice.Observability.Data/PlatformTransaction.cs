using Juice.MediatR;
using Juice.MediatR.Behaviors;
using Juice.Messaging;
using Juice.Messaging.Outbox;
using Microsoft.Extensions.Logging;

namespace Juice.Observability.Data
{
    /// <summary>
    /// Every command's transaction: the handler changes entities and raises domain events, and this saves them, dispatches the events, saves the
    /// outbox and commits, in that order (Juice <see cref="TransactionBehavior{TRequest, TResponse, TContext}"/>). Handlers never begin, save or commit.
    /// </summary>
    public sealed class PlatformTransactionBehavior<TRequest, TResponse>(PlatformDbContext db, IOutboxService<PlatformDbContext> outbox, IMediator mediator,
        ILogger<PlatformTransactionBehavior<TRequest, TResponse>> logger, IPostCommitActions? postCommitActions = null)
        : TransactionBehavior<TRequest, TResponse, PlatformDbContext>(db, outbox, mediator, logger, postCommitActions)
        where TRequest : IRequest<TResponse>;

    /// <summary>
    /// The ambient <see cref="MessageContext"/> every entry point needs before sending a command that may publish (a worker's pass, a request).
    /// Starts one when none is active and clears it after; inside an active one it does nothing, so nested sends keep the caller's correlation.
    /// </summary>
    public readonly struct PlatformMessageContext : IDisposable
    {
        private readonly bool _started;

        private PlatformMessageContext(bool started) => _started = started;

        /// <param name="source">What started the work: a worker's name, or <c>api</c>.</param>
        /// <param name="correlationId">The request's correlation id, so the outbox carries it on; null for a new one.</param>
        public static PlatformMessageContext Begin(string source, string? correlationId = null)
        {
            if (MessageContext.IsInitialized)
            {
                return new PlatformMessageContext(false);
            }
            MessageContext.Initialize(correlationId ?? Guid.NewGuid().ToString(), causationId: null, executionId: Guid.NewGuid().ToString(), source: source);
            return new PlatformMessageContext(true);
        }

        public void Dispose()
        {
            if (_started)
            {
                MessageContext.Clear();
            }
        }
    }
}
