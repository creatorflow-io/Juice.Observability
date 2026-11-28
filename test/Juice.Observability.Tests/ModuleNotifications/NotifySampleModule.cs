using System.Text.Json;
using Juice.Notifications;
using Juice.Notifications.Types;
using Juice.Observability.Extensibility;
using Juice.Observability.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests.ModuleNotifications
{
    /// <summary>The test module <c>notify-sample</c>: one notification type under its prefix, and the channels and types a test sets before composing it.</summary>
    public class NotifySampleModule : IObservabilityModule
    {
        public const string Name = "notify-sample";
        public const string Ping = "observability.notify-sample.ping";

        /// <summary>What the next composed <see cref="NotifySampleModule"/> declares; reset by <see cref="Use"/>.</summary>
        // Per thread: composition runs on the test's own thread, and the suite runs its classes in parallel.
        [ThreadStatic]
        private static Func<IEnumerable<TypeDeclaration>>? _types;

        [ThreadStatic]
        private static Action<IServiceCollection>? _channels;

        public static Func<IEnumerable<TypeDeclaration>> Types { get => _types ?? (() => [Declare(Ping)]); set => _types = value; }

        public static Action<IServiceCollection>? Channels { get => _channels; set => _channels = value; }

        public virtual ModuleInfo Info { get; } = new(Name, "Notify sample", "1.0.0", "[2.0.0,3.0.0)");

        /// <summary>Sets what the next module declares, and puts it back when disposed.</summary>
        public static IDisposable Use(Func<IEnumerable<TypeDeclaration>>? types = null, Action<IServiceCollection>? channels = null)
        {
            Types = types ?? (() => [Declare(Ping)]);
            Channels = channels;
            return new Reset();
        }

        public virtual void ConfigureServices(IModuleServicesBuilder module)
        {
            module.AddNotificationTypes(Types().ToArray());
            if (Channels is not null)
            {
                module.AddNotificationChannels(Channels);
            }
        }

        /// <summary>A valid type of the module's own: sent, correlated nowhere, rendered by no template (email only if one is declared).</summary>
        public static TypeDeclaration Declare(string key)
            => new(key, "Notify sample", "tests", null, null, null, true, JsonSerializer.SerializeToElement(new { type = "object" }), CorrelationRules.Optional, null, null,
                JsonSerializer.SerializeToElement(new { email = "Subject: Ping\n\nPing." }));

        private sealed class Reset : IDisposable
        {
            public void Dispose()
            {
                _types = null;
                _channels = null;
            }
        }
    }

    /// <summary>The same module under another name, for the cases that need two.</summary>
    public sealed class NotifyOtherModule : NotifySampleModule
    {
        public override ModuleInfo Info { get; } = new("notify-other", "Notify other", "1.0.0", "[2.0.0,3.0.0)");
    }

    /// <summary>A module named like a core type's family, for the key rule: <c>observability.alert.firing</c> starts with its prefix.</summary>
    public sealed class AlertNamedModule : NotifySampleModule
    {
        public override ModuleInfo Info { get; } = new("alert", "Alert named", "1.0.0", "[2.0.0,3.0.0)");
    }

    /// <summary>Declares valid types, then fails: what it declared goes with it.</summary>
    public sealed class DeclaresThenFailsModule : NotifySampleModule
    {
        public override void ConfigureServices(IModuleServicesBuilder module)
        {
            module.AddNotificationTypes(Declare("observability.notify-sample.leaked"));
            throw new InvalidOperationException("after declaring");
        }
    }
}
