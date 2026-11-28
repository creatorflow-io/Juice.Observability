using Juice.Observability.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Api.Modules
{
    /// <summary>
    /// A module's <see cref="ILeasedWorker"/> on the core's loop (<see cref="LeasedWorkerService"/>): one replica at a time, the same lease, pulse,
    /// passes and retries as discovery. Each pass gets a scope and a new instance of the worker.
    /// </summary>
    internal sealed class LeasedWorkerHost<T> : LeasedWorkerService where T : class, ILeasedWorker
    {
        private TimeSpan? _interval;

        public LeasedWorkerHost(string name, IServiceScopeFactory scopes, ILoggerFactory loggers)
            : base(name, scopes, loggers.CreateLogger($"Juice.Observability.Modules.Workers.{name}"))
        {
        }

        protected override TimeSpan Interval => _interval ??= ReadInterval();

        private TimeSpan ReadInterval()
        {
            using var scope = Scopes.CreateScope();
            var interval = scope.ServiceProvider.GetRequiredService<T>().Interval;
            return interval > TimeSpan.Zero ? interval : throw new InvalidOperationException($"The interval of worker '{Worker}' must be above zero.");
        }

        protected override async Task<LeasedPassOutcome> RunOnceAsync(LeasedPass pass, CancellationToken cancellationToken)
        {
            await using var scope = Scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<T>().RunOnceAsync(pass, cancellationToken);
        }

        protected override void LogFailure(Exception exception)
            => Logger.LogError(exception, "Worker {Worker} pass failed; retrying in {Interval}", Worker, Interval);
    }

    /// <summary>Starts the loop of each leased worker a module composed (<see cref="LeasedWorkerModuleExtensions.AddLeasedWorker{T}"/>).</summary>
    internal sealed class LeasedWorkerHostFactory(IServiceProvider services) : ILeasedWorkerHostFactory
    {
        public IHostedService Create(LeasedWorkerRegistration registration)
        {
            WorkerPulse.Declare(registration.Name);
            return (IHostedService)ActivatorUtilities.CreateInstance(services, typeof(LeasedWorkerHost<>).MakeGenericType(registration.WorkerType), registration.Name);
        }
    }
}
