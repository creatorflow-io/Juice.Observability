using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.MediatR;
using Juice.Notifications;
using Juice.Notifications.Configuration;
using Juice.Notifications.Providers.Chat;
using Juice.Notifications.Rendering;
using Juice.Observability.Data;
using Juice.Observability.Notifications;
using Juice.Observability.Notifications.Shadow;
using Juice.Observability.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// <c>Webhooks:ConsoleUrl</c> (M53): refused at start unless it is a plain absolute http(s) base, and an accepted value builds the same
    /// link bytes in legacy and in the new pipeline, the value not normalised.
    /// </summary>
    [Trait("Milestone", "M53")]
    public sealed class ConsoleUrlTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public static TheoryData<string> Refused => new()
        {
            "obs.example.com", "/console", "ftp://x", "https://u:p@x", "https://x/?a=1", "https://x/#f", " https://x", "https://x\n", "https://x\t/",
            "https://" + new string('a', 2049 - "https://".Length),
        };

        [Theory]
        [MemberData(nameof(Refused))]
        public void ConsoleUrl_Refused_AtStart(string value)
        {
            var result = new WebhookOptionsValidation().Validate(null, new WebhookOptions { ConsoleUrl = value });

            Assert.True(result.Failed);
            Assert.Contains("Webhooks:ConsoleUrl", result.FailureMessage);
            // The value (its userinfo above all) is never repeated.
            Assert.DoesNotContain("u:p", result.FailureMessage);
            Assert.DoesNotContain(value.Trim(), result.FailureMessage, StringComparison.Ordinal);
        }

        [Fact]
        public void ConsoleUrl_RefusedValue_StopsTheHostAtStart()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IValidateOptions<WebhookOptions>, WebhookOptionsValidation>();
            services.Configure<WebhookOptions>(o => o.ConsoleUrl = "https://u:p@x");

            var ex = Assert.Throws<OptionsValidationException>(() => services.BuildServiceProvider().GetRequiredService<IOptions<WebhookOptions>>().Value);
            Assert.Contains("Webhooks:ConsoleUrl", ex.Message);
        }

        [Fact]
        public async Task ConsoleUrl_Refused_FailsTheHostsStart()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Webhooks:ConsoleUrl"] = "https://u:p@x" });

            var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            var text = ex.ToString();
            Assert.Contains("Webhooks:ConsoleUrl", text);
            Assert.DoesNotContain("u:p", text, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("HTTPS://Obs.EXAMPLE.com")]
        [InlineData("https://obs.example.com:443")]
        [InlineData("https://obs.example.com/")]
        [InlineData("https://obs.example.com/console/")]
        [InlineData("http://10.0.0.5:8080")]
        [InlineData("http://obs.example.com:80/")]
        public void ConsoleUrl_Accepted_AtStart(string value)
            => Assert.True(new WebhookOptionsValidation().Validate(null, new WebhookOptions { ConsoleUrl = value }).Succeeded);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ConsoleUrl_Empty_Valid(string? value)
            => Assert.True(new WebhookOptionsValidation().Validate(null, new WebhookOptions { ConsoleUrl = value }).Succeeded);

        private static string DeclaredTemplate(string type, string channel)
            => ObservabilityTypes.All().Single(t => t.Key == type).Templates!.Value.GetProperty(channel).GetString()!;

        private static async Task<string> RenderAsync(string channel, JsonElement data, Guid deliveryId, IReadOnlyDictionary<string, string> links)
        {
            await TemplateWarmUp.EnsureAsync();
            var model = new RenderModel(ObservabilityTypes.AlertFiring, 1, "Alert", NotificationPriority.High, data, new Dictionary<string, string>(), links,
                Delivery: new RenderDelivery(deliveryId), Escalation: null);
            return await TemplateRenderer.RenderAsync(DeclaredTemplate(ObservabilityTypes.AlertFiring, channel), model, Ct);
        }

        /// <summary>The raw value is the link's base on both sides: not lower-cased, no port dropped, the path kept, one trailing slash trimmed.</summary>
        [Theory]
        [InlineData("HTTPS://Obs.EXAMPLE.com")]
        [InlineData("https://obs.example.com:443")]
        [InlineData("https://obs.example.com/")]
        [InlineData("https://obs.example.com/console/")]
        [InlineData("http://10.0.0.5:8080")]
        public async Task ConsoleUrl_Accepted_LinkBytesEqual_LegacyAndNew(string raw)
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Webhooks:ConsoleUrl"] = raw, ["Notifications:Pipeline"] = "legacy" })
                .WithWebHostBuilder(b => b.ConfigureServices(s => s.PostConfigure<PlatformNotificationsOptions>(o => o.Pipeline = NotificationsPipeline.New)));
            _ = factory.Server;
            var (alert, transition) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(factory, database, transition);
            var request = Assert.Single(await ProducerAdapterTests.RequestsAsync(database));

            // Legacy's payload: today's, its id first (the request carries it without).
            var legacy = new JsonObject { ["id"] = Guid.CreateVersion7().ToString() };
            foreach (var p in request.Data.EnumerateObject())
            {
                legacy[p.Name] = JsonNode.Parse(p.Value.GetRawText());
            }
            var payload = legacy.ToJsonString();
            var view = NotificationView.Parse(payload);
            var expectedLink = $"{raw.TrimEnd('/')}/alerts/{alert}";
            Assert.Equal(expectedLink, request.Links["self"]);

            // Webhook: the same bytes (no Console link in its body, in either).
            var webhook = await RenderAsync(Channels.Webhook, request.Data, Guid.Parse(legacy["id"]!.GetValue<string>()), request.Links);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload), JsonNode.Parse(webhook)), webhook);
            Assert.DoesNotContain(raw, webhook, StringComparison.OrdinalIgnoreCase);   // its link is the relative one, in both

            var slack = ChannelContent.Chat(await RenderAsync("slack", request.Data, Guid.CreateVersion7(), request.Links), "x", request.Links, null);
            var teams = ChannelContent.Chat(await RenderAsync("teams", request.Data, Guid.CreateVersion7(), request.Links), "x", request.Links, null);
            var email = ChannelContent.Email(await RenderAsync(Channels.Email, request.Data, Guid.CreateVersion7(), request.Links), "x");
            Assert.Contains($"<{expectedLink}|", JsonDocument.Parse(ChannelFormatter.SlackBody(view, raw)).RootElement.GetProperty("blocks")[1].GetProperty("text").GetProperty("text").GetString());
            Assert.Equal(expectedLink, JsonSerializer.Deserialize<ChatContent>(slack)!.Url);
            Assert.Contains($"\"url\":\"{expectedLink}\"", JsonSerializer.Serialize(JsonDocument.Parse(ChannelFormatter.TeamsBody(view, raw)).RootElement.GetProperty("attachments")[0].GetProperty("content").GetProperty("actions")[0]));
            Assert.Equal(expectedLink, JsonSerializer.Deserialize<ChatContent>(teams)!.Url);
            Assert.Contains(expectedLink, ChannelFormatter.Email(view, raw).Body);
            Assert.Contains(expectedLink, JsonSerializer.Deserialize<EmailContent>(email)!.Text);

            // And the shadow comparer finds no difference on any of them.
            Assert.Null(ShadowBodies.Formatted(ChannelKind.Slack, payload, slack, raw));
            Assert.Null(ShadowBodies.Formatted(ChannelKind.Teams, payload, teams, raw));
            Assert.Null(ShadowBodies.Formatted(ChannelKind.Email, payload, email, raw));
        }

        [Fact]
        public async Task ConsoleUrl_Empty_NoLinkEitherSide()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await using var factory = new ApiTests.ApiFactory(database, settings: new Dictionary<string, string> { ["Webhooks:ConsoleUrl"] = "", ["Notifications:Pipeline"] = "legacy" })
                .WithWebHostBuilder(b => b.ConfigureServices(s => s.PostConfigure<PlatformNotificationsOptions>(o => o.Pipeline = NotificationsPipeline.New)));
            _ = factory.Server;
            var (_, transition) = await ProducerAdapterTests.FireAsync(database);
            await ProducerAdapterTests.PlanAsync(factory, database, transition);
            var request = Assert.Single(await ProducerAdapterTests.RequestsAsync(database));
            Assert.Empty(request.Links);

            var legacy = new JsonObject { ["id"] = Guid.CreateVersion7().ToString() };
            foreach (var p in request.Data.EnumerateObject())
            {
                legacy[p.Name] = JsonNode.Parse(p.Value.GetRawText());
            }
            var payload = legacy.ToJsonString();
            var view = NotificationView.Parse(payload);
            Assert.DoesNotContain("/alerts/", ChannelFormatter.SlackBody(view, ""));
            Assert.DoesNotContain("/alerts/", ChannelFormatter.Email(view, "").Body);
            var slack = ChannelContent.Chat(await RenderAsync("slack", request.Data, Guid.CreateVersion7(), request.Links), "x", request.Links, null);
            var email = ChannelContent.Email(await RenderAsync(Channels.Email, request.Data, Guid.CreateVersion7(), request.Links), "x");
            Assert.Null(JsonSerializer.Deserialize<ChatContent>(slack)!.Url);
            Assert.DoesNotContain("/alerts/", JsonSerializer.Deserialize<EmailContent>(email)!.Text);
            Assert.Null(ShadowBodies.Formatted(ChannelKind.Slack, payload, slack, ""));
            Assert.Null(ShadowBodies.Formatted(ChannelKind.Email, payload, email, ""));
        }
    }
}
