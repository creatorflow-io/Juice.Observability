using System.Text.Json;
using System.Text.Json.Nodes;
using Juice.Notifications;
using Juice.Notifications.Configuration;
using Juice.Notifications.Planning;
using Juice.Notifications.Providers;
using Juice.Notifications.Providers.Chat;
using Juice.Observability.Data;

namespace Juice.Observability.Notifications.Migration
{
    /// <summary>
    /// What the mapping needs from the deployment: the notifications delivery settings (which secrets the API may name), and the SMTP server and
    /// sender an email endpoint sends through (legacy <c>Webhooks:Smtp</c> is the deployment's one server; notifications names it in
    /// <c>Delivery:SmtpServers</c>).
    /// </summary>
    public sealed record EndpointMappingOptions(DeliveryOptions Delivery, string? SmtpServer = null, string? SmtpFrom = null);

    /// <summary>
    /// A legacy endpoint as an integration, its targets and a route (phase-5-plan I66, design §17), and back. Pure.
    /// <list type="bullet">
    /// <item>The integration, the route and the first target take the endpoint's key; an email endpoint's further recipients are targets
    /// <c>{key}-2</c>, <c>{key}-3</c>… (a target holds one address), listed by the route in the endpoint's order.</item>
    /// <item>The route matches <c>type</c> from <c>Events</c>, and the labels the producer adapter sets: <c>rule in(…)</c>,
    /// <c>environment in(…)</c>, <c>severity in(…)</c> (the levels at or above the minimum; none for <c>Info</c>, which is every level). An
    /// empty list matches any, as in legacy; a host alert has no environment label, so a list of environments leaves it out, as in legacy.</item>
    /// <item><c>Enabled</c> is the route's: the integration and its targets are always enabled.</item>
    /// <item>Secret references keep their text (<see cref="SecretMapping"/>); no secret is read.</item>
    /// </list>
    /// <see cref="Unmap"/> is the exact inverse of <see cref="Map"/> for every definition <see cref="Map"/> accepts whose URL and addresses are
    /// already in the form notifications stores (an absolute URI as <see cref="Uri.AbsoluteUri"/> writes it, lowercase email addresses).
    /// </summary>
    public static class EndpointMapping
    {
        public const string WebhookKind = "webhook";
        public const string SmtpKind = "smtp";

        /// <summary>The longest group window legacy takes, and a route's digestSeconds (M46-1).</summary>
        public const int MaxDigestSeconds = 3600;

        /// <summary>The key of an email endpoint's <paramref name="index"/>th address's target (0 is the endpoint's own key).</summary>
        public static string TargetKey(string endpointKey, int index) => index == 0 ? endpointKey : $"{endpointKey}-{index + 1}";

        public static Mapped<EndpointSetup> Map(LegacyEndpoint e, EndpointMappingOptions options)
        {
            var problems = new List<MappingProblem>();
            if (e.Key.Length > 0 && char.IsAsciiDigit(e.Key[0]))
            {
                // Decision G4 (Q14): legacy allows a digit first, notifications doesn't; an admin renames the endpoint.
                problems.Add(new(ProblemCodes.KeyStartsWithDigit, $"The key '{e.Key}' starts with a digit; a notifications key starts with a letter. Re-create the endpoint under another key."));
            }
            else if (!ConfigurationRules.Key().IsMatch(e.Key))
            {
                problems.Add(new(ProblemCodes.InvalidKey, $"The key '{e.Key}' isn't a notifications key: lowercase letters, digits and hyphens, starting with a letter. Re-create the endpoint under another key."));
            }
            // The group window becomes the route's digestSeconds (decision G2, M46-1). Mapped here in full; the planner reports it as
            // pending_feature until the notifications alpha takes it.
            if (e.GroupWindowSeconds is < 0 or > MaxDigestSeconds)
            {
                problems.Add(new(ProblemCodes.GroupWindow, $"The group window is {e.GroupWindowSeconds} s; it is 0–{MaxDigestSeconds}."));
            }
            var types = new List<string>();
            foreach (var @event in e.Events)
            {
                if (AliasVocabulary.TypeOf(@event) is { } type && !types.Contains(type))
                {
                    types.Add(type);
                }
                else
                {
                    problems.Add(new(ProblemCodes.Events, $"The event '{@event}' isn't one a route takes (alert.firing, alert.resolved, once each)."));
                }
            }
            if (types.Count == 0)
            {
                problems.Add(new(ProblemCodes.Events, "The endpoint takes no event."));
            }
            var labels = new JsonObject();
            In(labels, AliasVocabulary.Labels.Rule, e.RuleKeys, "rule key", problems);
            In(labels, AliasVocabulary.Labels.Environment, e.Environments, "environment", problems);
            if (e.MinimumSeverity != AlertSeverity.Info)
            {
                In(labels, AliasVocabulary.Labels.Severity, AliasVocabulary.Severities.Where(s => s >= e.MinimumSeverity).Select(AliasVocabulary.Label).ToList(), "severity", problems);
            }

            IntegrationDefinition? integration = null;
            var targets = new List<TargetDefinition>();
            switch (e.Kind)
            {
                case ChannelKind.Webhook:
                    var url = ConfigurationRules.Address(Channels.Webhook, e.Url);
                    if (url is null)
                    {
                        problems.Add(new(ProblemCodes.Recipients, "The URL isn't an absolute http or https URL without credentials."));
                    }
                    var signing = Secret(e.SecretRef, options, "signing secret", problems);
                    integration = new(e.Key, Channels.Webhook, WebhookKind, "{}", signing, 0, true);
                    targets.Add(new(e.Key, e.Key, url, null, true));
                    break;
                case ChannelKind.Slack or ChannelKind.Teams:
                    if (e.Url.Length > 0)
                    {
                        problems.Add(new(ProblemCodes.Recipients, "A chat endpoint's URL is behind its SecretRef; it has no Url."));
                    }
                    var address = Secret(e.SecretRef, options, "URL", problems);
                    integration = e.Kind == ChannelKind.Slack
                        ? new(e.Key, ChatChannels.Slack, SlackWebhookProvider.KindName, "{}", null, 0, true)
                        : new(e.Key, ChatChannels.Teams, TeamsWorkflowProvider.KindName, "{}", null, 0, true);
                    targets.Add(new(e.Key, e.Key, null, address, true));
                    break;
                case ChannelKind.Email:
                    if (options.SmtpServer is not { Length: > 0 } server || options.SmtpFrom is not { Length: > 0 } from)
                    {
                        problems.Add(new(ProblemCodes.SmtpServer, "An email endpoint needs the deployment's SMTP server named in notifications (Delivery:SmtpServers) and a sender."));
                        break;
                    }
                    if (e.Url.Length > 0 || e.SecretRef.Length > 0 || e.Recipients.Count is 0 or > 20)
                    {
                        problems.Add(new(ProblemCodes.Recipients, "An email endpoint has 1–20 recipients, and no Url or SecretRef."));
                    }
                    integration = new(e.Key, Channels.Email, SmtpKind, Canonical(new JsonObject { ["server"] = server, ["from"] = from }), null, 0, true);
                    for (var i = 0; i < e.Recipients.Count; i++)
                    {
                        var key = TargetKey(e.Key, i);
                        if (ConfigurationRules.Address(Channels.Email, e.Recipients[i]) is not { } mailbox)
                        {
                            problems.Add(new(ProblemCodes.Recipients, $"'{e.Recipients[i]}' isn't an email address notifications takes."));
                            continue;
                        }
                        if (!ConfigurationRules.Key().IsMatch(key))
                        {
                            problems.Add(new(ProblemCodes.InvalidKey, $"The recipient {i + 1}'s target key '{key}' is too long; shorten the endpoint's key."));
                        }
                        if (targets.Any(t => t.Address == mailbox))
                        {
                            problems.Add(new(ProblemCodes.Recipients, $"'{e.Recipients[i]}' is listed twice."));
                        }
                        targets.Add(new(e.Key, key, mailbox, null, true));
                    }
                    break;
                default:
                    problems.Add(new(ProblemCodes.Recipients, $"Unknown channel kind {e.Kind}."));
                    break;
            }
            if (problems.Count > 0 || integration is null)
            {
                return Mapped<EndpointSetup>.Refused(problems);
            }
            var match = new JsonObject { ["type"] = new JsonArray([.. types.Select(t => JsonValue.Create(t))]) };
            if (labels.Count > 0)
            {
                match["labels"] = labels;
            }
            var route = new RouteDefinition(e.Key, Canonical(match), targets.Select(t => t.Key).ToList(), e.Enabled, e.GroupWindowSeconds);
            return Mapped<EndpointSetup>.Ok(new EndpointSetup(integration, targets, route));
        }

        /// <summary>
        /// The legacy endpoint a setup stands for, or <see cref="ProblemCodes.NotAliasShaped"/> when it isn't one <see cref="Map"/> writes (a row
        /// edited outside the alias).
        /// </summary>
        public static Mapped<LegacyEndpoint> Unmap(EndpointSetup s)
        {
            static Mapped<LegacyEndpoint> NotOurs(string why) => Mapped<LegacyEndpoint>.Refused(new MappingProblem(ProblemCodes.NotAliasShaped, why));

            var (integration, route) = (s.Integration, s.Route);
            if (integration.Key != route.Key || integration.Priority != 0 || !integration.Enabled || s.Targets.Any(t => !t.Enabled || t.IntegrationKey != integration.Key))
            {
                return NotOurs("The integration, its targets and the route aren't the triple the alias writes.");
            }
            var expected = s.Targets.Select((t, i) => TargetKey(integration.Key, i)).ToList();
            if (!s.Targets.Select(t => t.Key).SequenceEqual(expected) || !route.Targets.SequenceEqual(expected))
            {
                return NotOurs("The route doesn't name the integration's targets in order.");
            }
            ChannelKind kind;
            string url = "", secret = "";
            var recipients = new List<string>();
            switch ((integration.Channel, integration.Kind))
            {
                case (Channels.Webhook, WebhookKind) when s.Targets is [{ Address: { } address, AddressRef: null }] && integration.SecretRef is { } signing
                        && Same(integration.Settings, "{}"):
                    (kind, url, secret) = (ChannelKind.Webhook, address, signing);
                    break;
                case (ChatChannels.Slack, SlackWebhookProvider.KindName) or (ChatChannels.Teams, TeamsWorkflowProvider.KindName)
                        when s.Targets is [{ Address: null, AddressRef: { } addressRef }] && integration.SecretRef is null && Same(integration.Settings, "{}"):
                    (kind, secret) = (integration.Channel == ChatChannels.Slack ? ChannelKind.Slack : ChannelKind.Teams, addressRef);
                    break;
                case (Channels.Email, SmtpKind) when s.Targets.Count > 0 && s.Targets.All(t => t is { Address: not null, AddressRef: null }) && integration.SecretRef is null:
                    kind = ChannelKind.Email;
                    recipients.AddRange(s.Targets.Select(t => t.Address!));
                    break;
                default:
                    return NotOurs($"A {integration.Channel} integration of kind {integration.Kind}, with these targets, isn't one the alias writes.");
            }

            using var document = JsonDocument.Parse(route.Match);
            var match = document.RootElement;
            var events = new List<string>();
            List<string> rules = [], environments = [];
            var minimum = AlertSeverity.Info;
            foreach (var property in match.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "type" when property.Value.ValueKind == JsonValueKind.Array:
                        foreach (var type in property.Value.EnumerateArray())
                        {
                            if (type.ValueKind != JsonValueKind.String || AliasVocabulary.EventOf(type.GetString()!) is not { } @event)
                            {
                                return NotOurs($"The route matches the type {type}, which no legacy event is.");
                            }
                            events.Add(@event);
                        }
                        break;
                    case "labels" when property.Value.ValueKind == JsonValueKind.Object:
                        foreach (var label in property.Value.EnumerateObject())
                        {
                            if (label.Value.ValueKind != JsonValueKind.String || !TryIn(label.Value.GetString()!, out var values))
                            {
                                return NotOurs($"The route's {label.Name} matcher isn't in(…).");
                            }
                            switch (label.Name)
                            {
                                case AliasVocabulary.Labels.Rule:
                                    rules = values;
                                    break;
                                case AliasVocabulary.Labels.Environment:
                                    environments = values;
                                    break;
                                case AliasVocabulary.Labels.Severity:
                                    var levels = AliasVocabulary.Severities.Select(AliasVocabulary.Label).ToList();
                                    var from = values.Count > 0 ? levels.IndexOf(values[0]) : -1;
                                    if (from <= 0 || !values.SequenceEqual(levels.Skip(from)))
                                    {
                                        return NotOurs("The route's severity isn't the levels at or above warning or critical.");
                                    }
                                    minimum = AliasVocabulary.Severities[from];
                                    break;
                                default:
                                    return NotOurs($"The route matches the label {label.Name}, which a legacy endpoint has no field for.");
                            }
                        }
                        break;
                    default:
                        return NotOurs($"The route's match has {property.Name}, which a legacy endpoint has no field for.");
                }
            }
            if (events.Count == 0)
            {
                return NotOurs("The route matches no type.");
            }
            return Mapped<LegacyEndpoint>.Ok(new LegacyEndpoint(integration.Key, kind, url, secret, events, rules, environments, recipients,
                route.DigestSeconds, minimum, route.Enabled));
        }

        private static string? Secret(string reference, EndpointMappingOptions options, string what, List<MappingProblem> problems)
        {
            var mapped = SecretMapping.Map(reference, options.Delivery);
            problems.AddRange(mapped.Problems.Select(p => p with { Message = $"The {what}: {p.Message}" }));
            return mapped.Value;
        }

        private static void In(JsonObject labels, string label, IReadOnlyList<string> values, string what, List<MappingProblem> problems)
        {
            if (values.Count == 0)
            {
                return;
            }
            if (values.Any(v => v.Length == 0 || v.Contains(',') || v != v.Trim() || v.Contains(')')) || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            {
                problems.Add(new(ProblemCodes.Recipients, $"A {what} with a comma, a parenthesis or spaces around it, or listed twice, can't be matched by a route."));
                return;
            }
            var matcher = $"in({string.Join(',', values)})";
            if (matcher.Length > RouteMatch.MaxPatternLength)
            {
                problems.Add(new(ProblemCodes.Recipients, $"The {what} list is longer than a route matcher takes ({RouteMatch.MaxPatternLength} characters)."));
                return;
            }
            labels[label] = matcher;
        }

        private static bool TryIn(string matcher, out List<string> values)
        {
            values = [];
            if (!matcher.StartsWith("in(", StringComparison.Ordinal) || !matcher.EndsWith(')') || matcher.Length == 4)
            {
                return false;
            }
            values = [.. matcher[3..^1].Split(',')];
            return values.All(v => v.Length > 0 && v == v.Trim());
        }

        internal static string Canonical(JsonNode node)
        {
            using var document = JsonDocument.Parse(node.ToJsonString());
            return CanonicalJson.Write(document.RootElement);
        }

        /// <summary>Two JSON texts that mean the same (jsonb rewrites spacing, key order and number forms).</summary>
        internal static bool Same(string a, string b)
        {
            using var x = JsonDocument.Parse(a);
            using var y = JsonDocument.Parse(b);
            return CanonicalJson.WriteNormalized(x.RootElement) == CanonicalJson.WriteNormalized(y.RootElement);
        }
    }
}
