using System.Text.Json;
using Juice.Notifications.Providers;
using Juice.Observability.Data;
using Juice.Observability.Notifications.Migration;
using Juice.Observability.Webhooks;

namespace Juice.Observability.Tests
{
    /// <summary>A legacy endpoint as an integration, its targets and a route, and back (phase-5-plan I66; proof point 5's mapping half).</summary>
    public class EndpointMappingTests
    {
        internal static readonly DeliveryOptions Delivery = new() { AliasSecretEnvPrefix = "JUICE_WEBHOOK_", AliasSecretDirectory = "/run/secrets/juice" };

        internal static readonly EndpointMappingOptions Options = new(Delivery, "observability", "alerts@example.com");

        internal static LegacyEndpoint Endpoint(string key = "ops", ChannelKind kind = ChannelKind.Webhook, string? url = null, string? secret = null,
            string[]? events = null, string[]? rules = null, string[]? environments = null, string[]? recipients = null, int group = 0,
            AlertSeverity minimum = AlertSeverity.Info, bool enabled = true)
            => new(key, kind,
                url ?? (kind == ChannelKind.Webhook ? "https://receiver.example/hooks/juice" : ""),
                secret ?? (kind == ChannelKind.Email ? "" : "env:JUICE_WEBHOOK_OPS"),
                events ?? [NotificationPlan.Firing, NotificationPlan.Resolved], rules ?? [], environments ?? [],
                recipients ?? (kind == ChannelKind.Email ? ["oncall@example.com"] : []), group, minimum, enabled);

        private static EndpointSetup Map(LegacyEndpoint e)
        {
            var mapped = EndpointMapping.Map(e, Options);
            Assert.True(mapped.Succeeded, string.Join("; ", mapped.Problems.Select(p => p.Message)));
            return mapped.Value!;
        }

        private static void RoundTrips(LegacyEndpoint e)
        {
            var back = EndpointMapping.Unmap(Map(e));
            Assert.True(back.Succeeded, string.Join("; ", back.Problems.Select(p => p.Message)));
            Assert.Equal(e, back.Value);
        }

        public static TheoryData<LegacyEndpoint> Every => new()
        {
            Endpoint(),
            Endpoint(kind: ChannelKind.Slack, secret: "env:JUICE_WEBHOOK_SLACK_OPS"),
            Endpoint(kind: ChannelKind.Teams, secret: "file:/run/secrets/juice/teams-ops"),
            Endpoint(kind: ChannelKind.Email, recipients: ["oncall@example.com", "sre@example.com", "lead@example.com"]),
            Endpoint(events: [NotificationPlan.Firing]),
            Endpoint(events: [NotificationPlan.Resolved, NotificationPlan.Firing]),
            Endpoint(rules: ["orders-errors", "checkout-latency"]),
            Endpoint(environments: ["production", "staging"]),
            Endpoint(minimum: AlertSeverity.Warning),
            Endpoint(minimum: AlertSeverity.Critical, rules: ["a-rule"], environments: ["production"]),
            Endpoint(enabled: false),
            Endpoint(group: 30),
            Endpoint(secret: "file:/run/secrets/juice/ops-signing"),
        };

        [Theory]
        [MemberData(nameof(Every))]
        public void Every_field_maps_and_maps_back(LegacyEndpoint endpoint) => RoundTrips(endpoint);

        [Fact]
        public void A_webhook_is_a_signed_integration_a_target_at_its_url_and_a_route_of_one_key()
        {
            var setup = Map(Endpoint());

            Assert.Equal(new IntegrationDefinition("ops", "webhook", "webhook", "{}", "env:JUICE_WEBHOOK_OPS", 0, true), setup.Integration);
            Assert.Equal([new TargetDefinition("ops", "ops", "https://receiver.example/hooks/juice", null, true)], setup.Targets);
            Assert.Equal("ops", setup.Route.Key);
            Assert.Equal(["ops"], setup.Route.Targets);
            Assert.Equal("""{"type":["observability.alert.firing","observability.alert.resolved"]}""", setup.Route.Match);
        }

        [Theory]
        [InlineData(ChannelKind.Slack, "slack", "slack-webhook")]
        [InlineData(ChannelKind.Teams, "teams", "teams-workflow")]
        public void A_chat_channels_url_stays_a_secret_the_target_names(ChannelKind kind, string channel, string providerKind)
        {
            var setup = Map(Endpoint(kind: kind, secret: "env:JUICE_WEBHOOK_CHAT"));

            Assert.Equal((channel, providerKind, (string?)null), (setup.Integration.Channel, setup.Integration.Kind, setup.Integration.SecretRef));
            Assert.Equal([new TargetDefinition("ops", "ops", null, "env:JUICE_WEBHOOK_CHAT", true)], setup.Targets);
        }

        [Fact]
        public void An_email_endpoint_sends_through_the_deployments_server_to_a_target_per_recipient()
        {
            var setup = Map(Endpoint(kind: ChannelKind.Email, recipients: ["oncall@example.com", "sre@example.com"]));

            Assert.Equal(("email", "smtp", """{"from":"alerts@example.com","server":"observability"}"""),
                (setup.Integration.Channel, setup.Integration.Kind, setup.Integration.Settings));
            Assert.Equal(["ops", "ops-2"], setup.Targets.Select(t => t.Key));
            Assert.Equal(["oncall@example.com", "sre@example.com"], setup.Targets.Select(t => t.Address));
            Assert.Equal(["ops", "ops-2"], setup.Route.Targets);
        }

        [Theory]
        [InlineData(AlertSeverity.Info, null)]
        [InlineData(AlertSeverity.Warning, "in(warning,critical)")]
        [InlineData(AlertSeverity.Critical, "in(critical)")]
        public void The_minimum_severity_is_the_levels_at_or_above_it(AlertSeverity minimum, string? matcher)
        {
            using var match = JsonDocument.Parse(Map(Endpoint(minimum: minimum)).Route.Match);
            var labels = match.RootElement.TryGetProperty("labels", out var l) ? l : default;
            Assert.Equal(matcher, labels.ValueKind == JsonValueKind.Object && labels.TryGetProperty("severity", out var s) ? s.GetString() : null);
        }

        [Fact]
        public void Empty_rules_and_environments_match_any_and_lists_match_one_of()
        {
            Assert.DoesNotContain("labels", Map(Endpoint()).Route.Match);

            using var match = JsonDocument.Parse(Map(Endpoint(rules: ["a-rule", "b-rule"], environments: ["production"])).Route.Match);
            var labels = match.RootElement.GetProperty("labels");
            Assert.Equal("in(a-rule,b-rule)", labels.GetProperty("rule").GetString());
            Assert.Equal("in(production)", labels.GetProperty("environment").GetString());
        }

        [Fact]
        public void The_route_matches_as_legacy_planning_does()
        {
            // NotificationPlan.Plan's endpoint filter, against the route's match as notifications runs it.
            var endpoint = Endpoint(events: [NotificationPlan.Firing], rules: ["orders-errors"], environments: ["production"], minimum: AlertSeverity.Warning);
            using var json = JsonDocument.Parse(Map(endpoint).Route.Match);
            var (route, errors) = Juice.Notifications.Planning.RouteMatch.Parse(json.RootElement);
            Assert.Empty(errors);
            foreach (var type in new[] { AliasVocabulary.FiringType, AliasVocabulary.ResolvedType })
            foreach (var rule in new[] { "orders-errors", "other" })
            foreach (var environment in new[] { "production", "staging", null })
            foreach (var severity in AliasVocabulary.Severities)
            {
                var legacy = endpoint.Events.Contains(AliasVocabulary.EventOf(type)!) && (endpoint.RuleKeys.Count == 0 || endpoint.RuleKeys.Contains(rule))
                    && severity >= endpoint.MinimumSeverity && (endpoint.Environments.Count == 0 || environment is not null && endpoint.Environments.Contains(environment));
                var labels = new Dictionary<string, string> { ["rule"] = rule, ["severity"] = AliasVocabulary.Label(severity) };
                if (environment is not null)
                {
                    labels["environment"] = environment;
                }
                Assert.Equal(legacy, route!.Matches(type, labels, Juice.Notifications.NotificationPriority.Normal));
            }
        }

        public static TheoryData<LegacyEndpoint, string> Refused => new()
        {
            { Endpoint(key: "1-ops"), ProblemCodes.KeyStartsWithDigit },
            { Endpoint(key: "ops_x"), ProblemCodes.InvalidKey },
            { Endpoint(group: 3601), ProblemCodes.GroupWindow },
            { Endpoint(events: ["webhook.test"]), ProblemCodes.Events },
            { Endpoint(events: []), ProblemCodes.Events },
            { Endpoint(secret: "env:OTHER_SECRET"), ProblemCodes.SecretOutOfReach },
            { Endpoint(secret: "file:/etc/passwd"), ProblemCodes.SecretOutOfReach },
            { Endpoint(secret: "vault:x#y"), ProblemCodes.SecretRef },
            { Endpoint(url: "ftp://receiver.example/"), ProblemCodes.Recipients },
            { Endpoint(kind: ChannelKind.Slack, url: "https://hooks.slack.com/x"), ProblemCodes.Recipients },
            { Endpoint(kind: ChannelKind.Email, recipients: ["not an address"]), ProblemCodes.Recipients },
            { Endpoint(kind: ChannelKind.Email, recipients: ["a@example.com", "a@example.com"]), ProblemCodes.Recipients },
            { Endpoint(rules: ["a,b"]), ProblemCodes.Recipients },
        };

        [Theory]
        [MemberData(nameof(Refused))]
        public void What_a_route_cant_hold_is_refused_with_why(LegacyEndpoint endpoint, string code)
        {
            var mapped = EndpointMapping.Map(endpoint, Options);
            Assert.False(mapped.Succeeded);
            Assert.Contains(mapped.Problems, p => p.Code == code);
        }

        [Fact]
        public void An_email_endpoint_needs_the_smtp_server_named()
        {
            var mapped = EndpointMapping.Map(Endpoint(kind: ChannelKind.Email), Options with { SmtpServer = null });
            Assert.Contains(mapped.Problems, p => p.Code == ProblemCodes.SmtpServer);
        }

        [Fact]
        public void Rows_the_alias_didnt_write_arent_read_back_as_an_endpoint()
        {
            var setup = Map(Endpoint(minimum: AlertSeverity.Warning));
            static void NotOurs(EndpointSetup s) => Assert.Contains(EndpointMapping.Unmap(s).Problems, p => p.Code == ProblemCodes.NotAliasShaped);

            NotOurs(setup with { Route = setup.Route with { Match = """{"type":["observability.alert.firing"],"labels":{"service":"in(a)"}}""" } });
            NotOurs(setup with { Route = setup.Route with { Match = """{"type":["other.type"]}""" } });
            NotOurs(setup with { Route = setup.Route with { Match = """{"type":["observability.alert.firing"],"labels":{"severity":"in(info,warning,critical)"}}""" } });
            NotOurs(setup with { Route = setup.Route with { Match = """{"type":["observability.alert.firing"],"minPriority":"high"}""" } });
            NotOurs(setup with { Route = setup.Route with { Targets = ["other"] } });
            NotOurs(setup with { Integration = setup.Integration with { Priority = 1 } });
            NotOurs(setup with { Integration = setup.Integration with { Kind = "other" } });
            NotOurs(setup with { Targets = [setup.Targets[0] with { Enabled = false }] });
        }

        [Fact]
        public void A_match_read_back_from_the_database_in_another_form_still_maps_back()
        {
            // jsonb keeps neither spacing nor key order.
            var setup = Map(Endpoint(rules: ["a-rule"]));
            var stored = setup with { Route = setup.Route with { Match = """{ "labels": { "rule": "in(a-rule)" }, "type": [ "observability.alert.firing", "observability.alert.resolved" ] }""" } };
            Assert.Equal(Endpoint(rules: ["a-rule"]), EndpointMapping.Unmap(stored).Value);
        }
    }
}
