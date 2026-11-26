using Juice.MediatR;
using Juice.Messaging.Policies;
using Juice.Notifications.Data;
using Juice.Observability.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// The pipeline's messaging beside the platform's in one container (M45 coexistence decision, phase-5 waves §5.2): what the host works
    /// around until the upstream fixes reach published packages. The platform's own half is its own rule, not this: its transaction wraps only its
    /// commands (<see cref="PlatformCommandsTransaction{TRequest, TResponse}"/>).
    /// <list type="bullet">
    /// <item>The pipeline's <c>TransactionBehavior</c> wraps every command, so a platform command opened an empty pipeline transaction: here it
    /// wraps the pipeline's commands only (<see cref="PipelineCommandsOnly{TRequest, TResponse}"/>). Comes out with U2 (notifications).</item>
    /// <item>The publishing policies are merged, both answer <c>local</c> for every message, and Juice gave both routes, so each event had two
    /// outbox deliveries and was handled twice: here identical routes are given once (<c>DistinctRoutes</c>). Comes out with U1 (Juice).</item>
    /// </list>
    /// </summary>
    internal static class Coexistence
    {
        /// <summary>Call after both the platform's messaging and the pipeline are registered.</summary>
        public static void Isolate(IServiceCollection services)
        {
            var registered = services.Where(s => s.ServiceType == typeof(IPipelineBehavior<,>) && s.ImplementationType == typeof(NotificationsTransactionBehavior<,>))
                .ToList();
            if (registered.Count == 0)
            {
                throw new InvalidOperationException("NotificationsTransactionBehavior isn't registered: register the pipeline first.");
            }
            foreach (var descriptor in registered)
            {
                services.Remove(descriptor);
            }
            // The behavior itself, resolved only for the pipeline's commands.
            services.AddScoped(typeof(NotificationsTransactionBehavior<,>));
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(PipelineCommandsOnly<,>));

            var policy = services.LastOrDefault(s => s.ServiceType == typeof(IMessagePublishingPolicy))
                ?? throw new InvalidOperationException("The platform's messaging must be registered before the notification pipeline.");
            services.Remove(policy);
            services.AddSingleton<IMessagePublishingPolicy>(sp => new DistinctRoutes(Create(sp, policy)));
        }

        private static IMessagePublishingPolicy Create(IServiceProvider sp, ServiceDescriptor descriptor)
            => (IMessagePublishingPolicy)(descriptor.ImplementationInstance
                ?? descriptor.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));

        private sealed class DistinctRoutes(IMessagePublishingPolicy inner) : IMessagePublishingPolicy
        {
            public async ValueTask<IReadOnlyCollection<PublishRoute>> ResolveAsync(PolicyResolveContext context)
                => [.. (await inner.ResolveAsync(context)).Distinct()];
        }
    }

    /// <summary><see cref="NotificationsTransactionBehavior{TRequest, TResponse}"/> around the pipeline's commands, not the platform's.</summary>
    internal sealed class PipelineCommandsOnly<TRequest, TResponse>(IServiceProvider services) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public int Order => int.MaxValue - 20;

        public ValueTask<TResponse> Handle(TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
            => PlatformCommands.IsEmbeddedProducts(typeof(TRequest))
                ? services.GetRequiredService<NotificationsTransactionBehavior<TRequest, TResponse>>().Handle(request, next, cancellationToken)
                : next.Invoke(request, cancellationToken);
    }
}
