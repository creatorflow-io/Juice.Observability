using System.Text.Json;
using Juice.Observability.Data;
using Juice.Observability.Tests.Legacy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// <c>Notifications:ConsoleUrl</c> (M50, ported from M53's <c>Webhooks:ConsoleUrl</c>): refused at start unless it is a plain absolute http(s) base,
    /// and an accepted value builds the link the 1.x formatter built, byte for byte, the value not normalised.
    /// </summary>
    [Trait("Milestone", "M50")]
    public sealed class ConsoleUrlTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public static TheoryData<string> Refused => new()
        {
            "obs.example.com", "/console", "ftp://x", "https://u:p@x", "https://x/?a=1", "https://x/#f", " https://x", "https://x\n", "https://x\t/", "https://x/\u0001",
            "https://x y", "https://" + new string('a', 2049 - "https://".Length), "https://x/" + new string('a', 2039),
        };

        [Theory]
        [MemberData(nameof(Refused))]
        public void ConsoleUrl_Refuses_Relative_NoScheme_Userinfo_Query_Fragment_Whitespace_ControlChar_Over2048_ValueNotEchoed(string value)
        {
            var result = new PlatformNotificationsOptionsValidation().Validate(null, new PlatformNotificationsOptions { ConsoleUrl = value });

            Assert.True(result.Failed);
            Assert.Contains("Notifications:ConsoleUrl", result.FailureMessage);
            // The value (its userinfo above all) is never repeated.
            Assert.DoesNotContain("u:p", result.FailureMessage);
            Assert.DoesNotContain(value.Trim(), result.FailureMessage, StringComparison.Ordinal);
        }

        [Fact]
        public void ConsoleUrl_Accepts_2048Exactly()
        {
            var exactly = "https://console.example.com/" + new string('a', 2048 - "https://console.example.com/".Length);
            Assert.Equal(2048, exactly.Length);

            Assert.True(new PlatformNotificationsOptionsValidation().Validate(null, new PlatformNotificationsOptions { ConsoleUrl = exactly }).Succeeded);
            Assert.True(new PlatformNotificationsOptionsValidation().Validate(null, new PlatformNotificationsOptions { ConsoleUrl = exactly + "a" }).Failed);
        }

        [Theory]
        [InlineData("HTTPS://Obs.EXAMPLE.com")]
        [InlineData("https://obs.example.com:443")]
        [InlineData("https://obs.example.com/")]
        [InlineData("https://obs.example.com/console/")]
        [InlineData("http://10.0.0.5:8080")]
        [InlineData("http://obs.example.com:80/")]
        [InlineData(null)]
        [InlineData("")]
        public void ConsoleUrl_Accepts_AbsoluteHttpOrHttps_OrNothing(string? value)
            => Assert.True(new PlatformNotificationsOptionsValidation().Validate(null, new PlatformNotificationsOptions { ConsoleUrl = value }).Succeeded);

        [Fact]
        public void ConsoleUrl_RefusedValue_StopsTheOptionsAtStart()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IValidateOptions<PlatformNotificationsOptions>, PlatformNotificationsOptionsValidation>();
            services.Configure<PlatformNotificationsOptions>(o => o.ConsoleUrl = "https://u:p@x");

            var ex = Assert.Throws<OptionsValidationException>(() => services.BuildServiceProvider().GetRequiredService<IOptions<PlatformNotificationsOptions>>().Value);
            Assert.Contains("Notifications:ConsoleUrl", ex.Message);
            Assert.DoesNotContain("u:p", ex.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ConsoleUrl_Refused_FailsTheHostsStart_WithoutTheValue()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Notifications:ConsoleUrl"] = "https://u:p@x" });

            var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            Assert.Contains("Notifications:ConsoleUrl", ex.ToString());
            Assert.DoesNotContain("u:p", ex.ToString(), StringComparison.Ordinal);
        }

        /// <summary>
        /// The link is <c>ConsoleUrl.TrimEnd('/') + "/" + page</c>: the raw text, a mixed-case host and a default port kept, the trailing slashes
        /// trimmed. The frozen 1.x formatter builds the same bytes, for the same address, in each channel.
        /// </summary>
        [Theory]
        [InlineData("HTTPS://Console.Example.COM:443//")]
        [InlineData("https://Console.Example.com:443")]
        [InlineData("http://Console.Example.com:80/")]
        [InlineData("https://console.example.com/ops/")]
        [InlineData("http://10.0.0.5:8080///")]
        [InlineData("https://console.example.com")]
        public void ConsoleLink_MixedCaseHost_DefaultPort443And80_TrailingSlashes_ByteEqualsFrozenFormatter(string raw)
        {
            Assert.True(new PlatformNotificationsOptionsValidation().Validate(null, new PlatformNotificationsOptions { ConsoleUrl = raw }).Succeeded);
            var rule = new Rule { TenantId = Guid.NewGuid(), Key = "orders-errors", Name = "Orders errors", Definition = "{}", DefinitionHash = "h" };
            var alert = new Alert
            {
                TenantId = rule.TenantId, RuleId = rule.Id, DedupKey = "d", SubjectKey = "service:shop/orders@production", Severity = AlertSeverity.Critical,
                State = AlertState.Firing, BreachStartedAt = DateTimeOffset.UtcNow, FiredAt = DateTimeOffset.UtcNow,
            };
            var transition = new AlertTransition { TenantId = rule.TenantId, AlertId = alert.Id, ToState = AlertState.Firing, At = DateTimeOffset.UtcNow, Value = 0.5, Reason = "x" };
            var view = NotificationView.Parse(Rules.Notifications.NotificationPlan.Payload(Guid.NewGuid(), Rules.Notifications.NotificationPlan.Firing, "t", rule, alert, transition, null));

            var link = new PlatformNotificationsOptions { ConsoleUrl = raw }.Link($"alerts/{alert.Id}");

            Assert.Equal($"{raw.TrimEnd('/')}/alerts/{alert.Id}", link);
            // The frozen formatter's own link, in each of its channels (the bodies are JSON: read, not searched for escaped text).
            var slack = JsonDocument.Parse(ChannelFormatter.SlackBody(view, raw)).RootElement.GetProperty("blocks")[1].GetProperty("text").GetProperty("text").GetString()!;
            Assert.Contains($"<{link}|", slack, StringComparison.Ordinal);
            Assert.Contains(link!, ChannelFormatter.Email(view, raw).Body, StringComparison.Ordinal);
            var card = JsonDocument.Parse(ChannelFormatter.TeamsBody(view, raw)).RootElement.GetProperty("attachments")[0].GetProperty("content");
            Assert.Equal(link, card.GetProperty("actions")[0].GetProperty("url").GetString());
            Assert.Null(new PlatformNotificationsOptions().Link("alerts/x"));
        }
    }
}
