using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Juice.Notifications;
using Juice.Notifications.Embedded;
using Juice.Notifications.Types;
using Juice.Observability.Api;
using Juice.Observability.Api.Modules;
using Juice.Observability.Extensibility;
using Juice.Observability.Notifications;
using Juice.Observability.Tests.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Juice.Observability.Tests.ModuleNotifications
{
    /// <summary>A module's notification types and channels (core 2.0 M3, E8): what is refused, what a refusal leaves, and that the core's own declarations are unchanged. No database.</summary>
    [Trait("Milestone", "C2-M3")]
    public sealed class ModuleNotificationTests
    {
        private static WebApplicationBuilder Core()
        {
            var builder = CoreHost.CreateBuilder();
            builder.Services.AddJuiceObservability(builder.Configuration);
            return builder;
        }

        private static NotificationsEmbeddedOptions Embedded(IServiceCollection services)
            => (NotificationsEmbeddedOptions)services.Single(s => s.ServiceType == typeof(NotificationsEmbeddedOptions)).ImplementationInstance!;

        private static ModuleCompositionException Refused<T>(WebApplicationBuilder builder) where T : IObservabilityModule, new()
            => Assert.Throws<ModuleCompositionException>(() => builder.AddObservabilityModule<T>());

        private static string[] Keys(IServiceCollection services) => Embedded(services).Types.Select(t => t.Key!).ToArray();

        private static string Hash(IEnumerable<TypeDeclaration> types)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", types.Select(t => JsonSerializer.Serialize(t))))));

        private static TypeDeclaration Declare(string key) => NotifySampleModule.Declare(key);

        private static ChannelDefinition Channel(string key) => new(key, AddressKind.None, default, ContentShape.Body, false, false);

        // ─── Types ─────────────────────────────────────────────────────────────

        [Fact]
        public void Type_key_needs_module_prefix_or_moved_key()
        {
            var builder = Core();
            var before = Keys(builder.Services);
            using (NotifySampleModule.Use(() => [Declare("observability.other.ping")]))
            {
                Assert.Contains("observability.notify-sample.", Refused<NotifySampleModule>(builder).Message);
            }
            using (NotifySampleModule.Use(() => [Declare("observability.notify-sample.")]))
            {
                Refused<NotifySampleModule>(builder);
            }
            using (NotifySampleModule.Use(() => [Declare("notify-sample.ping"), Declare("observability.notify-sampler.ping")]))
            {
                Refused<NotifySampleModule>(builder);
            }
            Assert.Equal(before, Keys(builder.Services));

            builder.AddObservabilityModule<NotifySampleModule>();
            Assert.Equal([.. before, NotifySampleModule.Ping], Keys(builder.Services));
        }

        [Fact]
        public void Core_type_key_refused()
        {
            var builder = Core();
            // The prefix rule alone would let a module named "alert" declare the alert types.
            using (NotifySampleModule.Use(() => [Declare(ObservabilityTypes.AlertFiring)]))
            {
                var e = Refused<AlertNamedModule>(builder);
                Assert.Contains("core type", e.Message);
                Assert.Contains(ObservabilityTypes.AlertFiring, e.Message);
            }
            using (NotifySampleModule.Use(() => [Declare(ObservabilityTypes.AlertResolved)]))
            {
                Assert.Contains("core type", Refused<NotifySampleModule>(builder).Message);
            }
            Assert.Equal(ObservabilityTypes.All().Select(t => t.Key), Keys(builder.Services));
        }

        [Fact]
        public void Same_type_two_modules_refused()
        {
            var builder = Core();
            builder.AddObservabilityModule<NotifySampleModule>();
            // The second module declares what the first did: refused for the prefix or for the key's being taken, either way nothing is added.
            using (NotifySampleModule.Use(() => [Declare(NotifySampleModule.Ping)]))
            {
                var e = Refused<NotifyOtherModule>(builder);
                Assert.Contains(NotifySampleModule.Ping, e.Message);
            }
            Assert.Single(Keys(builder.Services), k => k == NotifySampleModule.Ping);
        }

        [Fact]
        public void Moved_key_claimed_twice_refused()
        {
            var builder = Core();
            // The agents module claimed the action request types as it composed.
            Assert.Equal(ObservabilityTypes.ActionRequests().Select(t => t.Key), Keys(builder.Services).Skip(2));
            using (NotifySampleModule.Use(() => [Declare(ObservabilityTypes.ActionApproval)]))
            {
                var e = Refused<NotifySampleModule>(builder);
                Assert.Contains("claimed", e.Message);
                Assert.Contains("agents", e.Message);
            }
            using (NotifySampleModule.Use(() => [Declare(ObservabilityTypes.ForPlatformEvent(Juice.Observability.Data.ActionRequestEvents.Created))]))
            {
                Assert.Contains("claimed", Refused<NotifySampleModule>(builder).Message);
            }
        }

        [Fact]
        public void A_type_declared_twice_in_a_call_or_in_two_calls_refused_and_nothing_added()
        {
            var builder = Core();
            var before = Keys(builder.Services);
            using (NotifySampleModule.Use(() => [Declare(NotifySampleModule.Ping), Declare(NotifySampleModule.Ping)]))
            {
                Refused<NotifySampleModule>(builder);
            }
            // A good type then a bad one in one call: the good one isn't kept.
            using (NotifySampleModule.Use(() => [Declare(NotifySampleModule.Ping), Declare("observability.other.x")]))
            {
                Refused<NotifySampleModule>(builder);
            }
            Assert.Equal(before, Keys(builder.Services));
            Assert.DoesNotContain(builder.Services.Select(d => d.ImplementationInstance).OfType<ModuleNotificationTypes>(), r => r.Module == NotifySampleModule.Name);
        }

        [Fact]
        public void More_than_fifty_types_refused()
        {
            var builder = Core();
            using (NotifySampleModule.Use(() => Enumerable.Range(0, 51).Select(i => Declare($"observability.notify-sample.t{i}"))))
            {
                Assert.Contains("50", Refused<NotifySampleModule>(builder).Message);
            }
            using (NotifySampleModule.Use(() => Enumerable.Range(0, 50).Select(i => Declare($"observability.notify-sample.t{i}"))))
            {
                builder.AddObservabilityModule<NotifySampleModule>();
            }
            Assert.Equal(2 + 8 + 50, Keys(builder.Services).Length);
        }

        [Fact]
        public void A_module_refused_after_it_declared_leaves_no_type_behind()
        {
            var builder = Core();
            var before = Keys(builder.Services);
            Refused<DeclaresThenFailsModule>(builder);
            // Its types were appended as it went; the next declaration, or the pipeline's start, takes them back.
            builder.AddObservabilityModule<NotifySampleModule>();
            Assert.Equal([.. before, NotifySampleModule.Ping], Keys(builder.Services));
        }

        [Fact]
        public void A_builder_that_is_not_the_hosts_is_refused()
        {
            var builder = Core();
            Assert.Throws<ModuleCompositionException>(() => new Impostor(builder.Services).AddNotificationTypes(Declare("observability.notify-sample.x")));
            Assert.Throws<ModuleCompositionException>(() => new Impostor(builder.Services).AddNotificationChannels(s => s.AddSingleton(Channel("pager"))));
        }

        private sealed class Impostor(IServiceCollection services) : IModuleServicesBuilder
        {
            public ModuleInfo Module { get; } = new(NotifySampleModule.Name, "Impostor", "1.0.0", "[2.0.0,3.0.0)");
            public IServiceCollection Services { get; } = services;
            public Microsoft.Extensions.Configuration.IConfiguration Configuration => throw new NotSupportedException();
            public IModuleServicesBuilder AddWorker<TWorker>() where TWorker : class, Microsoft.Extensions.Hosting.IHostedService => this;
            public IModuleServicesBuilder AddMigrations<TContext>() where TContext : Microsoft.EntityFrameworkCore.DbContext => this;
            public IModuleServicesBuilder AddPolicy(string name, params string[] roles) => this;
            public IModuleServicesBuilder SetConsoleManifest(ConsoleManifest manifest) => this;
        }

        [Fact]
        public void Embedded_types_equal_base_order_and_hash()
        {
            var builder = Core();
            var embedded = Embedded(builder.Services).Types;
            var all = ObservabilityTypes.All().ToList();
            Assert.Equal(all.Select(t => t.Key), embedded.Select(t => t.Key));
            Assert.Equal(Hash(all), Hash(embedded));
            // Pinned from base (the declarations as the core made them, before the agents module declared the action requests): a change here is a new type version.
            Assert.True(BaseHash == Hash(embedded), Hash(embedded));
            Assert.Equal(CoreMovedNotificationTypes.All.Order(), ObservabilityTypes.ActionRequests().Select(t => t.Key).Order());
            Assert.Equal(["observability.alert.firing", "observability.alert.resolved"], ObservabilityTypes.Core().Select(t => t.Key));
        }

        // The SHA-256 of ObservabilityTypes.All() as base 5eb6188 serialized it, one declaration a line.
        private const string BaseHash = "AE36995A7DE0D6099CC23650A01A55EEECC33BC00F1853AD3407ABF57738E2CD";

        // ─── Channels ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData("webhook")]
        [InlineData("email")]
        [InlineData("inbox")]
        public void Builtin_channel_keys_refused(string key)
        {
            var builder = Core();
            var before = builder.Services.Count;
            using (NotifySampleModule.Use(channels: s => s.AddSingleton(Channel(key))))
            {
                Assert.Contains("core channel", Refused<NotifySampleModule>(builder).Message);
            }
            Assert.Equal(before, builder.Services.Count);
        }

        [Theory]
        [InlineData("slack")]
        [InlineData("teams")]
        public void Core_chat_key_refused(string key)
        {
            var builder = Core();
            using (NotifySampleModule.Use(channels: s => s.AddSingleton(Channel(key))))
            {
                Assert.Contains("core channel", Refused<NotifySampleModule>(builder).Message);
            }
        }

        [Fact]
        public void Factory_channel_descriptor_refused()
        {
            var builder = Core();
            var before = builder.Services.Count;
            using (NotifySampleModule.Use(channels: s => s.AddSingleton(_ => Channel("notify-sample-pager"))))
            {
                Assert.Contains("instance", Refused<NotifySampleModule>(builder).Message);
            }
            using (NotifySampleModule.Use(channels: s => s.AddKeyedSingleton("k", Channel("notify-sample-pager"))))
            {
                Assert.Contains("instance", Refused<NotifySampleModule>(builder).Message);
            }
            // Registered under another service type, the registry would never see it: not a way past the key rules.
            using (NotifySampleModule.Use(channels: s => s.AddSingleton<object>(Channel("webhook"))))
            {
                Refused<NotifySampleModule>(builder);
            }
            Assert.Equal(before, builder.Services.Count);
        }

        [Theory]
        [InlineData("")]
        [InlineData("p")]
        [InlineData("Pager")]
        [InlineData("1pager")]
        [InlineData("pager\n")]
        [InlineData("pa ger")]
        [InlineData("pa_ger")]
        [InlineData("a234567890123456789012345678901234")]
        public void Channel_key_shape_refused(string key)
        {
            var builder = Core();
            using (NotifySampleModule.Use(channels: s => s.AddSingleton(Channel(key))))
            {
                Assert.Contains("must match", Refused<NotifySampleModule>(builder).Message);
            }
        }

        [Fact]
        public void A_channel_key_registered_before_refused_and_a_valid_channel_copied()
        {
            var builder = Core();
            using (NotifySampleModule.Use(channels: s =>
            {
                s.AddSingleton(Channel("notify-sample-pager"));
                s.AddSingleton<Recorder>();
            }))
            {
                builder.AddObservabilityModule<NotifySampleModule>();
            }
            var channels = builder.Services.Where(d => d.ImplementationInstance is ChannelDefinition).Select(d => ((ChannelDefinition)d.ImplementationInstance!).Key).ToList();
            Assert.Contains("notify-sample-pager", channels);
            Assert.Contains(builder.Services, d => d.ServiceType == typeof(Recorder));

            // The key is taken now: by this module again, by another, and twice in one call.
            using (NotifySampleModule.Use(() => [], s => s.AddSingleton(Channel("notify-sample-pager"))))
            {
                Assert.Contains("already registered", Refused<NotifyOtherModule>(builder).Message);
            }
            var before = builder.Services.Count;
            using (NotifySampleModule.Use(() => [], s => { s.AddSingleton(Channel("notify-other-a")); s.AddSingleton(Channel("notify-other-a")); }))
            {
                Assert.Contains("already registered", Refused<NotifyOtherModule>(builder).Message);
            }
            Assert.Equal(before, builder.Services.Count);
        }

        [Fact]
        public void A_refused_channel_adds_none_of_its_registrations()
        {
            var builder = Core();
            var before = builder.Services.Count;
            using (NotifySampleModule.Use(channels: s =>
            {
                s.AddSingleton<Recorder>();
                s.AddSingleton(Channel("notify-sample-pager"));
                s.AddSingleton(Channel("webhook"));
            }))
            {
                Refused<NotifySampleModule>(builder);
            }
            Assert.Equal(before, builder.Services.Count);
            Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(Recorder));
        }

        private sealed class Recorder;
    }
}
