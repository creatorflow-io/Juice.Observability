using Juice.Messaging.Policies;
using Juice.Observability.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Notifications
{
    /// <summary>
    /// The pipeline's messaging beside the platform's in one container (M45 coexistence decision, phase-5 waves §5.2): what the host still works
    /// around. The pipeline keeps out of the host's commands and messages itself (its gate, M44-c), and the platform's transaction wraps only the
    /// platform's commands (<see cref="PlatformCommandsTransaction{TRequest, TResponse}"/>, its own rule). Left: the publishing policies are
    /// merged, and the platform's catch-all <c>local</c> one answers for the pipeline's messages too, so a message given the same route twice
    /// would be delivered twice. Identical routes are given once here (<c>DistinctRoutes</c>), until a Juice release with distinct routes (#130)
    /// is pinned.
    /// </summary>
    internal static class Coexistence
    {
        /// <summary>Call after both the platform's messaging and the pipeline are registered.</summary>
        public static void Isolate(IServiceCollection services)
        {
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
}

namespace Juice.Observability.Notifications
{
    /// <summary>The pipeline's commands, by its own list (<see cref="Juice.Notifications.Data.NotificationsCommands"/>): the platform's transaction leaves them to the pipeline's.</summary>
    internal sealed class PipelineCommands(Juice.Notifications.Data.NotificationsCommands commands) : IEmbeddedCommands
    {
        public bool Owns(Type request) => commands.Owns(request);
    }
}
