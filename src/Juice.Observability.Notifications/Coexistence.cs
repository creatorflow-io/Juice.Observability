using Juice.MediatR;
using Juice.Messaging.Policies;
using Juice.Notifications.Data;
using Juice.Observability.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// The platform's messaging and the pipeline's in one container (M45 coexistence). Both register the same Juice pieces, which apply to every
    /// command and message unless told otherwise:
    /// <list type="bullet">
    /// <item>each <c>TransactionBehavior</c> wraps every command, so a platform command opened an empty pipeline transaction and the reverse;
    /// here each wraps its own product's commands only;</item>
    /// <item>the publishing policies are merged, and both answer <c>local</c> for every message, so each event had two outbox deliveries and
    /// was handled twice over; here identical routes are given once.</item>
    /// </list>
    /// </summary>
    internal static class Coexistence
    {
        /// <summary>A command of the pipeline's own (its assemblies are <c>Juice.Notifications.*</c>): the pipeline's transaction is its own.</summary>
        public static bool IsPipelines(Type request)
            => request.Assembly.GetName().Name is { } name && (name == "Juice.Notifications" || name.StartsWith("Juice.Notifications.", StringComparison.Ordinal));

        /// <summary>Call after both the platform's messaging and the pipeline are registered.</summary>
        public static void Isolate(IServiceCollection services)
        {
            Scope(services, typeof(PlatformTransactionBehavior<,>), typeof(PlatformCommandsOnly<,>));
            Scope(services, typeof(NotificationsTransactionBehavior<,>), typeof(PipelineCommandsOnly<,>));
            var policy = services.LastOrDefault(s => s.ServiceType == typeof(IMessagePublishingPolicy))
                ?? throw new InvalidOperationException("The platform's messaging must be registered before the notification pipeline.");
            services.Remove(policy);
            services.AddSingleton<IMessagePublishingPolicy>(sp => new DistinctRoutes(Create(sp, policy)));
        }

        private static void Scope(IServiceCollection services, Type behavior, Type scoped)
        {
            var registered = services.Where(s => s.ServiceType == typeof(IPipelineBehavior<,>) && s.ImplementationType == behavior).ToList();
            if (registered.Count == 0)
            {
                throw new InvalidOperationException($"{behavior.Name} isn't registered: register the platform's messaging and the pipeline first.");
            }
            foreach (var descriptor in registered)
            {
                services.Remove(descriptor);
            }
            // The behavior itself, resolved only for the commands it owns.
            services.AddScoped(behavior);
            services.AddScoped(typeof(IPipelineBehavior<,>), scoped);
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

    /// <summary><see cref="PlatformTransactionBehavior{TRequest, TResponse}"/> around the platform's commands, not the pipeline's.</summary>
    internal sealed class PlatformCommandsOnly<TRequest, TResponse>(IServiceProvider services) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public int Order => int.MaxValue - 20;

        public ValueTask<TResponse> Handle(TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
            => Coexistence.IsPipelines(typeof(TRequest))
                ? next.Invoke(request, cancellationToken)
                : services.GetRequiredService<PlatformTransactionBehavior<TRequest, TResponse>>().Handle(request, next, cancellationToken);
    }

    /// <summary><see cref="NotificationsTransactionBehavior{TRequest, TResponse}"/> around the pipeline's commands, not the platform's.</summary>
    internal sealed class PipelineCommandsOnly<TRequest, TResponse>(IServiceProvider services) : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public int Order => int.MaxValue - 20;

        public ValueTask<TResponse> Handle(TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
            => Coexistence.IsPipelines(typeof(TRequest))
                ? services.GetRequiredService<NotificationsTransactionBehavior<TRequest, TResponse>>().Handle(request, next, cancellationToken)
                : next.Invoke(request, cancellationToken);
    }
}
