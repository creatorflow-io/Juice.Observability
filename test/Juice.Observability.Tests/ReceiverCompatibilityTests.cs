using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Juice.Observability.Data;
using Juice.Observability.Rules.Notifications;
using Juice.Observability.Webhooks;
using Microsoft.EntityFrameworkCore;
using Flow = Juice.Observability.Tests.NotificationFlowTests;
using Receiver = Juice.Observability.Tests.WebhookTests.Receiver;

namespace Juice.Observability.Tests
{
    /// <summary>
    /// Proof point 7 (phase-5-plan; waves §5.3, M46b): a receiver written against today's webhooks can't tell which pipeline sent to it. The
    /// sample receiver below is one such receiver, written from the documented contract rather than from our code: it verifies the Standard
    /// Webhooks signature with its own HMAC, rejects stale timestamps, processes each <c>webhook-id</c> once, and reads bodies of the legacy
    /// shape, with a known <c>type</c> and an <c>id</c> unique per notification. Each scenario runs on the old pipeline and, once it can send
    /// (Observability M45-1), on the new one, against the same receiver and the same secret.
    /// </summary>
    public sealed class ReceiverCompatibilityTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>The secret the receiver was given when it was set up, and the endpoint's secret reference names.</summary>
        private const string Secret = "flow-secret";

        /// <summary>The receiver's own variable: NotificationFlowTests sets its shared one to chat URLs while other classes run.</summary>
        private const string SecretVariable = "JUICE_WEBHOOK_RECEIVER_COMPAT";

        static ReceiverCompatibilityTests() => Environment.SetEnvironmentVariable(SecretVariable, Secret);

        public enum Pipeline { Old, New }

        private static void Require(Pipeline pipeline)
        {
            LocalStack.RequireAvailable();
            Assert.SkipWhen(pipeline == Pipeline.New,
                "the new pipeline sends Observability's notifications from M45-1 (obs #7): this row runs once its adapter is merged");
        }

        // ─── The sample receiver ───────────────────────────────────────────────

        /// <summary>A receiver as an integrator writes it from the webhook documentation (requirements §8), independent of our signer.</summary>
        internal sealed class SampleReceiver(string secret)
        {
            private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
            private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

            /// <summary>The bodies processed, once each.</summary>
            public List<JsonElement> Processed { get; } = [];

            /// <summary>Requests acknowledged without processing: an id seen before (a retry).</summary>
            public int Duplicates { get; private set; }

            /// <summary>Takes one request as received; null when accepted (processed or a duplicate), else why it was rejected.</summary>
            public string? Take(Receiver.Received request, DateTimeOffset now)
            {
                if (string.IsNullOrEmpty(request.Id) || !long.TryParse(request.Timestamp, out var timestamp))
                {
                    return "missing webhook-id or webhook-timestamp";
                }
                if ((now - DateTimeOffset.FromUnixTimeSeconds(timestamp)).Duration() > Tolerance)
                {
                    return "timestamp outside the replay window";
                }
                var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{request.Id}.{request.Timestamp}.{request.Body}"));
                var expected = Encoding.ASCII.GetBytes("v1," + Convert.ToBase64String(mac));
                if (!request.Signature.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Any(candidate => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(candidate))))
                {
                    return "signature doesn't verify";
                }
                if (!_seen.Add(request.Id))
                {
                    Duplicates++;
                    return null;
                }
                using var document = JsonDocument.Parse(request.Body);
                Processed.Add(document.RootElement.Clone());
                return null;
            }

            public void TakeAll(Receiver receiver)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var request in receiver.Requests)
                {
                    Assert.Null(Take(request, now));
                }
            }
        }

        // ─── The shape the receiver reads ──────────────────────────────────────

        private static readonly string[] AlertEnvelope = ["alert", "escalation", "id", "links", "occurredAt", "tenant", "type"];
        private static readonly string[] AlertFields =
            ["breachStartedAt", "firedAt", "id", "reason", "resolvedAt", "rule", "ruleName", "severity", "slo", "state", "subject", "value"];
        private static readonly string[] GroupEnvelope = ["alerts", "count", "event", "id", "occurredAt", "tenant", "type"];
        private static readonly string[] Types = [NotificationPlan.Firing, NotificationPlan.Resolved, NotificationPlan.Escalated];

        private static string[] Names(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

        /// <summary>An alert notification as the receiver reads it: the envelope, its alert, a known type and a GUID id.</summary>
        private static void IsAlertBody(JsonElement body)
        {
            Assert.Equal(AlertEnvelope, Names(body));
            Assert.Equal(AlertFields, Names(body.GetProperty("alert")));
            Assert.Contains(body.GetProperty("type").GetString(), Types);
            Assert.True(Guid.TryParse(body.GetProperty("id").GetString(), out _));
            Assert.True(body.GetProperty("occurredAt").TryGetDateTimeOffset(out _));
            Assert.Equal(JsonValueKind.String, body.GetProperty("links").GetProperty("alert").ValueKind);
        }

        private static void IdsAreUnique(SampleReceiver sample)
            => Assert.Equal(sample.Processed.Count, sample.Processed.Select(b => b.GetProperty("id").GetString()).Distinct().Count());

        // ─── Scenarios ─────────────────────────────────────────────────────────

        /// <summary>
        /// Firing, a retry after a 503, then resolved: every request verifies with the receiver's secret, the retry repeats the id (and the
        /// receiver processes it once), and the two notifications have the alert shape, their types and different ids.
        /// </summary>
        [Theory]
        [InlineData(Pipeline.Old)]
        [InlineData(Pipeline.New)]
        public async Task A_firing_retried_and_resolved_alert_reads_as_before(Pipeline pipeline)
        {
            Require(pipeline);
            await using var receiver = await Receiver.StartAsync(503);
            await using var database = await LocalStack.CreateDatabaseAsync();
            await Flow.EndpointAsync(database, receiver, secretVariable: SecretVariable);
            var dispatcher = Flow.Dispatcher(database);

            var alert = await Flow.FireAsync(database);
            await DrainAsync(dispatcher, receiver, 2);
            await Flow.ResolveAsync(database, alert);
            await DrainAsync(dispatcher, receiver, 3);

            var sample = new SampleReceiver(Secret);
            sample.TakeAll(receiver);
            Assert.Equal(1, sample.Duplicates);
            Assert.Equal([NotificationPlan.Firing, NotificationPlan.Resolved], sample.Processed.Select(b => b.GetProperty("type").GetString()));
            Assert.All(sample.Processed, IsAlertBody);
            IdsAreUnique(sample);
            // The retry is the same notification: the same id and the same bytes.
            var requests = receiver.Requests.ToList();
            Assert.Equal((requests[0].Id, requests[0].Body), (requests[1].Id, requests[1].Body));
            Assert.All(sample.Processed, b => Assert.Equal(JsonValueKind.Null, b.GetProperty("escalation").ValueKind));
        }

        /// <summary>An escalation step reads as an alert notification of type <c>alert.escalated</c> naming its policy and step, with its own id.</summary>
        [Theory]
        [InlineData(Pipeline.Old)]
        [InlineData(Pipeline.New)]
        public async Task An_escalation_step_reads_as_before(Pipeline pipeline)
        {
            Require(pipeline);
            await using var receiver = await Receiver.StartAsync();
            await using var pager = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await Flow.EndpointAsync(database, receiver, secretVariable: SecretVariable);
            await Flow.EndpointAsync(database, pager, "pager", events: [NotificationPlan.Resolved], secretVariable: SecretVariable);
            await using (var db = database.Context())
            {
                db.EscalationPolicies.Add(new EscalationPolicy
                {
                    TenantId = await Flow.TenantAsync(db), Key = "paging", Name = "Paging", UpdatedAt = DateTimeOffset.UtcNow,
                    Steps = [new EscalationStep { AfterMinutes = 5, EndpointKeys = ["pager"] }],
                });
                await db.SaveChangesAsync(Ct);
            }
            var (dispatcher, worker) = (Flow.Dispatcher(database), Flow.Worker(database));
            var alert = await Flow.FireAsync(database);
            await DrainAsync(dispatcher, receiver, 1);

            await Flow.AgeAsync(database, alert, 6);
            await worker.RunOnceAsync(Ct);
            await worker.RunOnceAsync(Ct);
            await DrainAsync(dispatcher, pager, 1);

            var sample = new SampleReceiver(Secret);
            sample.TakeAll(receiver);
            sample.TakeAll(pager);
            Assert.Equal(0, sample.Duplicates);
            Assert.All(sample.Processed, IsAlertBody);
            IdsAreUnique(sample);
            var escalated = Assert.Single(sample.Processed, b => b.GetProperty("type").GetString() == NotificationPlan.Escalated);
            Assert.Equal(("paging", 1), (escalated.GetProperty("escalation").GetProperty("policy").GetString(), escalated.GetProperty("escalation").GetProperty("step").GetInt32()));
            var firing = Assert.Single(sample.Processed, b => b.GetProperty("type").GetString() == NotificationPlan.Firing);
            Assert.Equal(firing.GetProperty("alert").GetProperty("id").GetString(), escalated.GetProperty("alert").GetProperty("id").GetString());
        }

        /// <summary>What a group window gathered reads as one <c>alerts.grouped</c> notification of the alerts it holds, each of the alert shape.</summary>
        [Theory]
        [InlineData(Pipeline.Old)]
        [InlineData(Pipeline.New)]
        public async Task A_group_window_reads_as_before(Pipeline pipeline)
        {
            Require(pipeline);
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await Flow.EndpointAsync(database, receiver, group: 30, secretVariable: SecretVariable);
            for (var i = 0; i < 3; i++)
            {
                await Flow.FireAsync(database, $"service:s{i}@production", ruleKey: $"rule-{i}");
            }
            var dispatcher = Flow.Dispatcher(database);
            await dispatcher.DispatchOnceAsync(Ct);
            Assert.Empty(receiver.Requests);
            await using (var db = database.Context())
            {
                // The window passes.
                await db.WebhookDeliveries.ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
            }
            await DrainAsync(dispatcher, receiver, 1);

            var sample = new SampleReceiver(Secret);
            sample.TakeAll(receiver);
            var body = Assert.Single(sample.Processed);
            Assert.Equal(GroupEnvelope, Names(body));
            Assert.Equal((NotificationView.GroupType, NotificationPlan.Firing, 3), (body.GetProperty("type").GetString(), body.GetProperty("event").GetString(), body.GetProperty("count").GetInt32()));
            Assert.True(Guid.TryParse(body.GetProperty("id").GetString(), out _));
            Assert.All(body.GetProperty("alerts").EnumerateArray(), a => Assert.Equal(AlertFields, Names(a)));
            Assert.Equal(3, body.GetProperty("alerts").EnumerateArray().Select(a => a.GetProperty("id").GetString()).Distinct().Count());
        }

        /// <summary>A secret other than the endpoint's never verifies: the receiver would reject what it didn't set up.</summary>
        [Theory]
        [InlineData(Pipeline.Old)]
        [InlineData(Pipeline.New)]
        public async Task Another_secret_does_not_verify(Pipeline pipeline)
        {
            Require(pipeline);
            await using var receiver = await Receiver.StartAsync();
            await using var database = await LocalStack.CreateDatabaseAsync();
            await Flow.EndpointAsync(database, receiver, secretVariable: SecretVariable);
            var dispatcher = Flow.Dispatcher(database);
            await Flow.FireAsync(database);
            await DrainAsync(dispatcher, receiver, 1);

            var request = Assert.Single(receiver.Requests);
            Assert.Equal("signature doesn't verify", new SampleReceiver("another-secret").Take(request, DateTimeOffset.UtcNow));
            Assert.Equal("timestamp outside the replay window", new SampleReceiver(Secret).Take(request, DateTimeOffset.UtcNow.AddMinutes(10)));
            Assert.Null(new SampleReceiver(Secret).Take(request, DateTimeOffset.UtcNow));
        }

        private static async Task DrainAsync(WebhookDispatcher dispatcher, Receiver receiver, int requests)
        {
            for (var i = 0; i < 100 && receiver.Requests.Count < requests; i++)
            {
                await dispatcher.DispatchOnceAsync(Ct);
                await Task.Delay(20, Ct);
            }
            Assert.True(receiver.Requests.Count >= requests, $"the receiver got {receiver.Requests.Count} of {requests} requests");
        }
    }
}
