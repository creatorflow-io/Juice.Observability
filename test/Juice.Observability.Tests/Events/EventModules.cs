using System.Collections.Concurrent;
using Juice.Observability.Extensibility;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests.Events
{
    /// <summary>What the test modules' handlers received.</summary>
    internal static class EventProbe
    {
        public static ConcurrentQueue<ObservabilityEvent> One { get; private set; } = new();

        public static ConcurrentQueue<ObservabilityEvent> Two { get; private set; } = new();

        /// <summary>How many more times module two's handler throws.</summary>
        public static int TwoFailures;

        public static void Reset()
        {
            One = new ConcurrentQueue<ObservabilityEvent>();
            Two = new ConcurrentQueue<ObservabilityEvent>();
            TwoFailures = 0;
        }
    }

    public sealed class OneChangeHandler : IObservabilityEventHandler<ChangeRecordedEvent>
    {
        public Task HandleAsync(ChangeRecordedEvent e, CancellationToken ct)
        {
            EventProbe.One.Enqueue(e);
            return Task.CompletedTask;
        }
    }

    public sealed class OneResourceHandler : IObservabilityEventHandler<ResourceChangedEvent>
    {
        public Task HandleAsync(ResourceChangedEvent e, CancellationToken ct)
        {
            EventProbe.One.Enqueue(e);
            return Task.CompletedTask;
        }
    }

    public sealed class OneAlertHandler : IObservabilityEventHandler<AlertTransitionedEvent>
    {
        public Task HandleAsync(AlertTransitionedEvent e, CancellationToken ct)
        {
            EventProbe.One.Enqueue(e);
            return Task.CompletedTask;
        }
    }

    public sealed class TwoChangeHandler : IObservabilityEventHandler<ChangeRecordedEvent>
    {
        public Task HandleAsync(ChangeRecordedEvent e, CancellationToken ct)
        {
            EventProbe.Two.Enqueue(e);
            return Interlocked.Decrement(ref EventProbe.TwoFailures) >= 0 ? throw new InvalidOperationException("module two is down") : Task.CompletedTask;
        }
    }

    public sealed class EventsOneModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("evone", "Ev one", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module)
            => module.AddEventHandler<ChangeRecordedEvent, OneChangeHandler>().AddEventHandler<ResourceChangedEvent, OneResourceHandler>()
                .AddEventHandler<AlertTransitionedEvent, OneAlertHandler>();
    }

    public sealed class EventsTwoModule : IObservabilityModule
    {
        public ModuleInfo Info { get; } = new("evtwo", "Ev two", "1.0.0", "[2.0.0,3.0.0)");

        public void ConfigureServices(IModuleServicesBuilder module) => module.AddEventHandler<ChangeRecordedEvent, TwoChangeHandler>();
    }
}
