using Juice.Observability.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// <c>Notifications:Inbox</c> (M54): the embedded inbox is opt-in, its roles are the Console's own, and a refusal at start names the key and never
    /// repeats the value.
    /// </summary>
    [Trait("Milestone", "M54")]
    public sealed class InboxOptionsTests
    {
        private const string Console = "https://console.example.com";

        private static PlatformNotificationsOptions Bind(Dictionary<string, string?> settings)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            return configuration.GetSection(PlatformNotificationsOptions.Section).Get<PlatformNotificationsOptions>() ?? new PlatformNotificationsOptions();
        }

        private static ValidateOptionsResult Validate(PlatformNotificationsOptions options)
            => new PlatformNotificationsOptionsValidation().Validate(null, options);

        [Fact]
        public void InboxOptions_Refuses_EnabledNoRoles_UnknownRole_UpperCaseRole_NoConsoleUrl_ValueNotEchoed()
        {
            // Enabled with no roles.
            var empty = Validate(new PlatformNotificationsOptions { ConsoleUrl = Console, Inbox = { Enabled = true, Roles = [] } });
            Assert.True(empty.Failed);
            Assert.Contains("Notifications:Inbox:Roles", empty.FailureMessage);

            // A role that isn't exactly viewer, operator or admin; the value is never repeated.
            foreach (var role in new[] { "Operator", "ADMIN", "superuser", "operatorx", "ops\nadmin", "secret-role-xyz" })
            {
                var refused = Validate(new PlatformNotificationsOptions { ConsoleUrl = Console, Inbox = { Enabled = true, Roles = ["admin", role] } });
                Assert.True(refused.Failed, role);
                Assert.Contains("Notifications:Inbox:Roles", refused.FailureMessage);
                Assert.DoesNotContain(role.Trim(), refused.FailureMessage, StringComparison.Ordinal);
            }

            // The same role twice would name a person's role twice.
            var twice = Validate(new PlatformNotificationsOptions { ConsoleUrl = Console, Inbox = { Enabled = true, Roles = ["admin", "admin"] } });
            Assert.True(twice.Failed);
            Assert.Contains("Notifications:Inbox:Roles", twice.FailureMessage);

            // The item's link needs the Console's address.
            foreach (var url in new[] { null, "" })
            {
                var noUrl = Validate(new PlatformNotificationsOptions { ConsoleUrl = url, Inbox = { Enabled = true } });
                Assert.True(noUrl.Failed);
                Assert.Contains("Notifications:ConsoleUrl", noUrl.FailureMessage);
            }

            Assert.True(Validate(new PlatformNotificationsOptions { ConsoleUrl = Console, Inbox = { Enabled = true } }).Succeeded);
            Assert.True(Validate(new PlatformNotificationsOptions { ConsoleUrl = Console, Inbox = { Enabled = true, Roles = ["viewer", "operator", "admin"] } }).Succeeded);
        }

        [Fact]
        public void InboxOptions_Off_ChangesNothing_WhateverTheKeysSay()
        {
            // Off is today's behaviour: nothing about the inbox is checked, and no Console address is needed.
            Assert.True(Validate(new PlatformNotificationsOptions()).Succeeded);
            Assert.True(Validate(new PlatformNotificationsOptions { Inbox = { Roles = ["bogus"] } }).Succeeded);
            Assert.False(new PlatformNotificationsOptions().Inbox.Enabled);
        }

        [Fact]
        public void InboxOptions_ConfiguredViewerOnly_IsExactlyViewer()
        {
            var options = Bind(new() { ["Notifications:Inbox:Enabled"] = "true", ["Notifications:Inbox:Roles:0"] = "viewer", ["Notifications:ConsoleUrl"] = Console });

            Assert.Equal(["viewer"], options.Inbox.EffectiveRoles);
            Assert.True(Validate(options).Succeeded);
        }

        [Fact]
        public void InboxOptions_KeyAbsent_DefaultsOperatorAdmin()
        {
            var options = Bind(new() { ["Notifications:Inbox:Enabled"] = "true", ["Notifications:ConsoleUrl"] = Console });

            Assert.Equal(["operator", "admin"], options.Inbox.EffectiveRoles);
            Assert.True(Validate(options).Succeeded);
        }

        [Fact]
        public void InboxOptions_NothingSet_IsOff()
        {
            var options = Bind(new() { ["Notifications:ConsoleUrl"] = Console });

            Assert.False(options.Inbox.Enabled);
        }
    }
}
