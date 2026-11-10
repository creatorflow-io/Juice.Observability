using System.Net;
using Juice.Observability.Api.Endpoints;
using Juice.Observability.Data;
using Microsoft.EntityFrameworkCore;
using ApiFactory = Juice.Observability.Tests.ApiTests.ApiFactory;

namespace Juice.Observability.Tests
{
    /// <summary>Phase 9 milestone 3 (R9): co-firing alerts are suggested with weighted evidence; an operator accepts or dismisses, audited; nothing merges on its own.</summary>
    public class CorrelationSuggestionTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static PairFacts Facts(TimeSpan apart, DependencyFact? dependency = null, string? host = null, int changes = 0, int unhealthy = 0)
            => new(apart, dependency, host, Enumerable.Range(0, changes).Select(i => new ChangeFact(DateTimeOffset.UnixEpoch.AddMinutes(i), $"change {i}")).ToList(),
                Enumerable.Range(0, unhealthy).Select(i => ($"ledger{i}", HealthState.Unhealthy)).ToList());

        private static readonly TimeSpan Far = TimeSpan.FromMinutes(10);

        [Fact]
        public void Each_piece_of_evidence_adds_its_weight_and_the_score_stops_at_one()
        {
            var dependency = new DependencyFact("orders", "payments", DependencyRelation.Calls, DependencyConfidence.Observed);
            Assert.Equal(0.40, CorrelationScorer.Score(Facts(Far, dependency)).Score);
            Assert.Equal(0.30, CorrelationScorer.Score(Facts(Far, host: "h1")).Score);
            Assert.Equal(0.30, CorrelationScorer.Score(Facts(Far, changes: 1)).Score);
            Assert.Equal(0.30, CorrelationScorer.Score(Facts(Far, changes: 5)).Score);   // one piece of evidence however many changes
            Assert.Equal(0.20, CorrelationScorer.Score(Facts(Far, unhealthy: 2)).Score);
            Assert.Equal(0, CorrelationScorer.Score(Facts(Far)).Score);
            Assert.Empty(CorrelationScorer.Score(Facts(Far)).Evidence);

            var all = CorrelationScorer.Score(Facts(TimeSpan.Zero, dependency, "h1", 4, 1));
            Assert.Equal(1, all.Score);
            Assert.Equal(["dependency", "same-host", "recent-change", "fired-together", "unhealthy-shared-dependency"], all.Evidence.Select(e => e.Code));
            Assert.Equal(1.4, all.Evidence.Sum(e => e.Weight), 3);   // every weight is shown, though the score is capped
            Assert.Contains("seen from both sides", all.Evidence[0].Text);
            Assert.Contains("4 changes from 30 minutes before the first breach to the second: change 3; change 2; change 1 and 1 more", all.Evidence[2].Text);
            Assert.Equal("Fired 0 s apart", all.Evidence[3].Text);
            // Observed only: a dependency only the caller reported adds nothing.
            Assert.Empty(CorrelationScorer.Score(Facts(Far, dependency with { Confidence = DependencyConfidence.Inferred })).Evidence);
        }

        [Theory]
        [InlineData(0, 0.20)]
        [InlineData(120, 0.20)]
        [InlineData(-120, 0.20)]
        [InlineData(360, 0.10)]
        [InlineData(510, 0.04)]
        [InlineData(600, 0)]
        public void Firing_together_counts_fully_within_two_minutes_then_slides_to_nothing_at_ten(int seconds, double weight)
            => Assert.Equal(weight, CorrelationScorer.Score(Facts(TimeSpan.FromSeconds(seconds))).Score);

        [Fact]
        public void A_pair_is_suggested_from_half()
        {
            Assert.True(CorrelationScorer.Suggested(CorrelationScorer.Score(Facts(TimeSpan.Zero, host: "h1")).Score));   // 0.3 + 0.2, despite floating point
            Assert.False(CorrelationScorer.Suggested(CorrelationScorer.Score(Facts(TimeSpan.FromSeconds(150), host: "h1")).Score));   // 0.3 + 0.19
            Assert.False(CorrelationScorer.Suggested(CorrelationScorer.Score(Facts(Far, changes: 1, unhealthy: 1)).Score - 0.01));
        }

        private sealed record Seeded(Guid Tenant, Guid A, Guid B, Guid C, Guid D, Guid E, Guid F, Guid H, Guid Foreign);

        /// <summary>
        /// Production: orders calls payments; both use ledger, which is unhealthy; orders, payments and stock run on host h1, search on h2; payments was
        /// deployed 10 minutes before. Firing: A orders, B payments (1 min later), C search, F host h1 (2 min later), H stock (1.5 min later).
        /// D is payments in staging, E a resolved orders alert, and one alert is another tenant's.
        /// </summary>
        private static async Task<Seeded> SeedAsync(ScratchDatabase database)
        {
            await using var db = database.Context();
            var tenant = (await db.Tenants.SingleAsync(Ct)).Id;
            var now = DateTimeOffset.UtcNow;
            var production = new DeploymentEnvironment { TenantId = tenant, Key = "production", Type = EnvironmentType.Production, FirstSeen = now, LastSeen = now };
            var staging = new DeploymentEnvironment { TenantId = tenant, Key = "staging", Type = EnvironmentType.Staging, FirstSeen = now, LastSeen = now };
            Service NewService(string name) => new() { TenantId = tenant, Namespace = "shop", Name = name, FirstSeen = now, LastSeen = now };
            var (orders, payments, ledger, search, stock, catalog) = (NewService("orders"), NewService("payments"), NewService("ledger"), NewService("search"), NewService("stock"), NewService("catalog"));
            Resource NewHost(string key) => new() { TenantId = tenant, Type = ResourceType.Host, Key = key, DisplayName = key, FirstSeen = now, LastSeen = now };
            var (h1, h2) = (NewHost("h1"), NewHost("h2"));
            ServiceInstance Instance(Service s, Resource host, DeploymentEnvironment e) => new()
            {
                TenantId = tenant, ServiceId = s.Id, EnvironmentId = e.Id, InstanceKey = $"{s.Name}-{e.Key}", HostResourceId = host.Id, FirstSeen = now, LastSeen = now,
            };
            Dependency Depends(Service from, Service to) => new()
            {
                TenantId = tenant, EnvironmentId = production.Id, SourceServiceId = from.Id, TargetServiceId = to.Id, Relation = DependencyRelation.Calls,
                Confidence = DependencyConfidence.Observed, EvidenceSource = "test", SpanCount = 1, FirstSeen = now, LastSeen = now,
            };
            Rule NewRule(Guid owner, string key, RuleTemplate template = RuleTemplate.ErrorRate) => new() { TenantId = owner, Key = key, Name = key, Template = template, Definition = "{}", DefinitionHash = "h", LoadedAt = now };
            var (errors, memory) = (NewRule(tenant, "errors"), NewRule(tenant, "host-memory", RuleTemplate.HostMemory));
            Alert NewAlert(Rule rule, Guid subject, HealthSubject type, DeploymentEnvironment? e, AlertSeverity severity, double firedMinutes, AlertState state = AlertState.Firing)
            {
                var fired = now.AddMinutes(-20 + firedMinutes);
                var key = type == HealthSubject.Host ? $"host:{subject}" : $"service:{subject}@{e!.Key}";
                return new()
                {
                    TenantId = rule.TenantId, RuleId = rule.Id, DedupKey = $"{rule.Key}|{key}|{Guid.NewGuid()}", SubjectKey = key, SubjectType = type, SubjectId = subject, EnvironmentId = e?.Id,
                    Severity = severity, State = state, BreachStartedAt = fired.AddMinutes(-2), FiredAt = fired, LastEvaluatedAt = now, ResolvedAt = state == AlertState.Resolved ? now : null,
                };
            }
            var a = NewAlert(errors, orders.Id, HealthSubject.Service, production, AlertSeverity.Warning, 0);
            var b = NewAlert(errors, payments.Id, HealthSubject.Service, production, AlertSeverity.Critical, 1);
            var c = NewAlert(errors, search.Id, HealthSubject.Service, production, AlertSeverity.Warning, 0.5);
            var d = NewAlert(errors, payments.Id, HealthSubject.Service, staging, AlertSeverity.Warning, 0);
            var e = NewAlert(errors, orders.Id, HealthSubject.Service, production, AlertSeverity.Warning, 0.5, AlertState.Resolved);
            var f = NewAlert(memory, h1.Id, HealthSubject.Host, null, AlertSeverity.Warning, 2);
            var h = NewAlert(errors, stock.Id, HealthSubject.Service, production, AlertSeverity.Warning, 1.5);
            var other = new PlatformTenant { Key = $"other-{Guid.NewGuid():N}", DisplayName = "Other" };
            var foreignRule = NewRule(other.Id, "errors");
            var foreign = NewAlert(foreignRule, orders.Id, HealthSubject.Service, production, AlertSeverity.Critical, 0);
            db.AddRange(production, staging, orders, payments, ledger, search, stock, catalog, h1, h2, errors, memory, other, foreignRule, a, b, c, d, e, f, h, foreign);
            db.AddRange(Instance(orders, h1, production), Instance(payments, h1, production), Instance(stock, h1, production), Instance(search, h2, production), Instance(payments, h2, staging));
            // Both also use catalog, which is healthy: a shared dependency only counts when it's unhealthy.
            db.AddRange(Depends(orders, payments), Depends(orders, ledger), Depends(payments, ledger), Depends(orders, catalog), Depends(payments, catalog));
            db.HealthSnapshots.Add(new HealthSnapshot { TenantId = tenant, SubjectType = HealthSubject.Service, SubjectId = ledger.Id, EnvironmentId = production.Id, State = HealthState.Unhealthy, PolicyVersion = "p", EvaluatedAt = now, Since = now });
            db.HealthSnapshots.Add(new HealthSnapshot { TenantId = tenant, SubjectType = HealthSubject.Service, SubjectId = catalog.Id, EnvironmentId = production.Id, State = HealthState.Healthy, PolicyVersion = "p", EvaluatedAt = now, Since = now });
            db.ChangeEvents.Add(new ChangeEvent { TenantId = tenant, Kind = ChangeKind.Deployment, Source = "pipeline", Summary = "payments 2.4.0", ServiceId = payments.Id, Environment = "production", OccurredAt = a.BreachStartedAt.AddMinutes(-10), RecordedAt = now });
            await db.SaveChangesAsync(Ct);
            return new(tenant, a.Id, b.Id, c.Id, d.Id, e.Id, f.Id, h.Id, foreign.Id);
        }

        private static List<(Guid Other, double Score)> Items(System.Text.Json.JsonElement suggestions)
            => suggestions.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("other").GetProperty("id").GetGuid(), i.GetProperty("score").GetDouble())).ToList();

        [Fact]
        public async Task Suggestions_carry_their_evidence_and_people_accept_or_dismiss_them_audited_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var s = await SeedAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, @operator, _, tenant) = await Caller.AllAsync(client, database);
            var alerts = $"/api/v1/tenants/{tenant}/alerts";

            // B shares everything; H (stock on h1) and F (host h1) only the host and timing, exactly the threshold, ties in firing order. C, D, E and the foreign alert aren't suggested.
            var first = await Caller.JsonAsync(await viewer.GetAsync($"{alerts}/{s.A}/suggestions"));
            Assert.Equal([(s.B, 1.0), (s.H, 0.5), (s.F, 0.5)], Items(first));
            Assert.Contains("not a proven cause", first.GetProperty("note").GetString());
            Assert.Equal("correlation", first.GetProperty("relation").GetString());
            var best = first.GetProperty("items")[0];
            Assert.Equal(["dependency", "same-host", "recent-change", "fired-together", "unhealthy-shared-dependency"],
                best.GetProperty("evidence").EnumerateArray().Select(x => x.GetProperty("code").GetString()));
            Assert.Contains("orders depends directly on payments", best.GetProperty("evidence")[0].GetProperty("text").GetString());
            Assert.Contains("payments 2.4.0", best.GetProperty("evidence")[2].GetProperty("text").GetString());
            Assert.Equal("Both depend on ledger (Unhealthy), which is unhealthy now", best.GetProperty("evidence")[4].GetProperty("text").GetString());   // not healthy catalog
            Assert.Equal(System.Text.Json.JsonValueKind.Null, best.GetProperty("incidentId").ValueKind);

            // Who may decide, and what is refused.
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"{alerts}/{s.A}/suggestions/{s.B}/accept", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await @operator.PostAsync($"{alerts}/{s.A}/suggestions/{s.H}/dismiss", new { reason = " " })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await @operator.PostAsync($"{alerts}/{s.A}/suggestions/{s.A}/accept", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await @operator.PostAsync($"{alerts}/{s.A}/suggestions/{s.C}/accept", new { })).StatusCode);   // timing alone
            Assert.Equal(HttpStatusCode.Conflict, (await @operator.PostAsync($"{alerts}/{s.A}/suggestions/{s.D}/accept", new { })).StatusCode);   // another environment
            Assert.Equal(HttpStatusCode.Conflict, (await @operator.PostAsync($"{alerts}/{s.A}/suggestions/{s.E}/accept", new { })).StatusCode);   // resolved
            Assert.Equal(HttpStatusCode.NotFound, (await @operator.PostAsync($"{alerts}/{s.A}/suggestions/{s.Foreign}/accept", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"{alerts}/{s.Foreign}/suggestions")).StatusCode);

            // Accepting opens an incident with both, as serious as the worse; a retry with the key gets the same answer, a second decision is refused.
            var accepted = await Caller.JsonAsync(await @operator.PostAsync($"{alerts}/{s.A}/suggestions/{s.B}/accept", new { }, key: "accept-1"));
            var incident = accepted.GetProperty("incidentId").GetGuid();
            Assert.Equal(("Accepted", 1.0, "phase9-1"), (accepted.GetProperty("decision").GetString(), accepted.GetProperty("score").GetDouble(), accepted.GetProperty("weightsVersion").GetString()));
            Assert.Equal(incident, (await Caller.JsonAsync(await @operator.PostAsync($"{alerts}/{s.A}/suggestions/{s.B}/accept", new { }, key: "accept-1"))).GetProperty("incidentId").GetGuid());
            Assert.Equal(HttpStatusCode.Conflict, (await @operator.PostAsync($"{alerts}/{s.B}/suggestions/{s.A}/dismiss", new { reason = "changed my mind" })).StatusCode);

            var detail = await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/incidents/{incident}"));
            Assert.Equal(("Critical", 2), (detail.GetProperty("incident").GetProperty("severity").GetString(), detail.GetProperty("alerts").GetArrayLength()));
            Assert.StartsWith("errors on service:", detail.GetProperty("incident").GetProperty("title").GetString());
            var events = detail.GetProperty("events").EnumerateArray().ToList();
            Assert.Equal(["created", "alert_added", "alert_added"], events.Select(x => x.GetProperty("type").GetString()));
            Assert.Equal("From a correlation suggestion, score 1.00", events[0].GetProperty("text").GetString());

            // The incident page suggests F and H, each with an alert already in it, and C with B (payments' deployment and timing: a change on either
            // counts, as the plan says), and accepting F adds it there.
            var forIncident = await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/incidents/{incident}/suggestions"));
            var outside = forIncident.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(new[] { s.C, s.F, s.H }.Order(), outside.Select(x => x.GetProperty("other").GetProperty("id").GetGuid()).Order());   // each once, though F and H pair with A and B
            // Each with its best pair: H and F with B (host, timing, payments' deployment), C with B.
            Assert.Equal([(s.H, 0.8), (s.F, 0.8), (s.C, 0.5)], outside.Select(x => (x.GetProperty("other").GetProperty("id").GetGuid(), x.GetProperty("score").GetDouble())));
            Assert.All(outside, x => Assert.Equal(s.B, x.GetProperty("alert").GetProperty("id").GetGuid()));
            Assert.All(outside, x => Assert.Equal(incident, x.GetProperty("incidentId").GetGuid()));
            var joined = await Caller.JsonAsync(await @operator.PostAsync($"{alerts}/{s.F}/suggestions/{s.A}/accept", new { title = "ignored: it joins the open incident" }));
            Assert.Equal(incident, joined.GetProperty("incidentId").GetGuid());
            Assert.Equal(3, (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/incidents/{incident}"))).GetProperty("alerts").GetArrayLength());

            // Dismissed with a reason, the pair isn't suggested again. B and F are with A in the incident now, so A has nothing left.
            var dismissed = await Caller.JsonAsync(await @operator.PostAsync($"{alerts}/{s.A}/suggestions/{s.H}/dismiss", new { reason = "stock's nightly import, ran --password hunter2" }));
            Assert.Equal("Dismissed", dismissed.GetProperty("decision").GetString());
            Assert.Empty(Items(await Caller.JsonAsync(await viewer.GetAsync($"{alerts}/{s.A}/suggestions"))));
            // B–H is its own pair: host, timing and payments' deployment (0.8); B–C deployment and timing (0.5).
            Assert.Equal([(s.H, 0.8), (s.C, 0.5)], Items(await Caller.JsonAsync(await viewer.GetAsync($"{alerts}/{s.B}/suggestions"))));

            await using var db = database.Context();
            var decisions = await db.CorrelationDecisions.AsNoTracking().OrderBy(x => x.DecidedAt).ToListAsync(Ct);
            Assert.Equal([CorrelationDecisionKind.Accepted, CorrelationDecisionKind.Accepted, CorrelationDecisionKind.Dismissed], decisions.Select(x => x.Decision));
            Assert.All(decisions, x => Assert.True(x.FirstAlertId.CompareTo(x.SecondAlertId) < 0));
            Assert.All(decisions, x => Assert.Equal("erin", x.DecidedBy));
            Assert.Equal(5, decisions[0].Evidence.Count);
            Assert.Equal(0.5, decisions[2].Score);
            Assert.DoesNotContain("hunter2", decisions[2].Reason);
            Assert.Null(decisions[2].IncidentId);

            var audit = await db.AuditRecords.AsNoTracking().Where(x => x.TargetType == "correlation").OrderBy(x => x.OccurredAt).ToListAsync(Ct);
            Assert.Equal(
                [("correlation.accept", AuditOutcome.Denied), ("correlation.accept", AuditOutcome.Failed), ("correlation.accept", AuditOutcome.Failed), ("correlation.accept", AuditOutcome.Failed),
                 ("correlation.accept", AuditOutcome.Succeeded), ("correlation.dismiss", AuditOutcome.Failed), ("correlation.accept", AuditOutcome.Succeeded), ("correlation.dismiss", AuditOutcome.Succeeded)],
                audit.Select(x => (x.Operation, x.Outcome)));
            Assert.Equal(["below_threshold", "not_candidates", "not_candidates", "decided"], audit.Where(x => x.Outcome == AuditOutcome.Failed).Select(x => x.Metadata["reason"]));
            Assert.Equal("dependency,same-host,recent-change,fired-together,unhealthy-shared-dependency", audit[4].Metadata["evidence"]);

            // Nothing about the alerts themselves changed.
            Assert.Equal(6, await db.Alerts.CountAsync(x => x.TenantId == tenant && x.State == AlertState.Firing && x.AcknowledgedAt == null, Ct));
        }

        /// <summary>
        /// Accepting and the incident endpoints take the same lock on an alert's membership, so an alert added to an incident by hand while a suggestion
        /// with it is being accepted ends up in one unresolved incident: the accept, run after, adds the other alert to it.
        /// </summary>
        [Fact]
        public async Task Accepting_waits_for_an_alert_being_added_to_an_incident_by_hand_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var s = await SeedAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, @operator, _, tenant) = await Caller.AllAsync(client, database);
            var (alerts, incidents) = ($"/api/v1/tenants/{tenant}/alerts", $"/api/v1/tenants/{tenant}/incidents");

            // Stand in for a slow manual add of B: hold B's membership lock, and add it to an incident in that transaction.
            await using var held = database.Context();
            await using var transaction = await held.Database.BeginTransactionAsync(Ct);
            await held.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({WorkerLease.KeyOf($"incident-membership:{s.B}")})", Ct);
            var accept = @operator.PostAsync($"{alerts}/{s.A}/suggestions/{s.B}/accept", new { });
            var create = @operator.PostAsync(incidents, new { title = "by hand too", alertIds = new[] { s.B } });
            await Task.Delay(1000, Ct);
            Assert.False(accept.IsCompleted, "accepting should wait for the lock");
            Assert.False(create.IsCompleted, "opening an incident should wait for the lock");
            var byHand = new Incident { TenantId = tenant, Title = "payments", State = IncidentState.Open, CreatedBy = "erin", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            held.Incidents.Add(byHand);
            held.IncidentAlerts.Add(new IncidentAlert { TenantId = tenant, IncidentId = byHand.Id, AlertId = s.B, AddedBy = "erin", AddedAt = DateTimeOffset.UtcNow });
            await held.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);

            var accepted = await Caller.JsonAsync(await accept);
            Assert.Equal(byHand.Id, accepted.GetProperty("incidentId").GetGuid());   // A joined B's incident
            Assert.Equal(HttpStatusCode.Conflict, (await create).StatusCode);   // B is taken
            await using var db = database.Context();
            Assert.Equal(1, await db.IncidentAlerts.CountAsync(l => l.AlertId == s.B, Ct));
            Assert.Equal(byHand.Id, (await db.IncidentAlerts.SingleAsync(l => l.AlertId == s.A, Ct)).IncidentId);
        }

        [Fact]
        public async Task Alerts_in_two_incidents_or_a_resolved_one_are_left_alone_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var s = await SeedAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, @operator, _, tenant) = await Caller.AllAsync(client, database);
            var (alerts, incidents) = ($"/api/v1/tenants/{tenant}/alerts", $"/api/v1/tenants/{tenant}/incidents");

            var one = (await Caller.JsonAsync(await @operator.PostAsync(incidents, new { title = "orders", alertIds = new[] { s.A } }))).GetProperty("id").GetGuid();
            await Caller.JsonAsync(await @operator.PostAsync(incidents, new { title = "payments", alertIds = new[] { s.B } }));
            Assert.DoesNotContain(s.B, Items(await Caller.JsonAsync(await viewer.GetAsync($"{alerts}/{s.A}/suggestions"))).Select(i => i.Other));
            var refused = await @operator.PostAsync($"{alerts}/{s.A}/suggestions/{s.B}/accept", new { });
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("different unresolved incident", await refused.Content.ReadAsStringAsync(Ct));

            // Resolved, the incident gives A back: suggested with B again (B's incident takes it), and a resolved incident suggests nothing.
            await Caller.JsonAsync(await @operator.PostAsync($"{incidents}/{one}/transitions", new { to = "resolved", comment = "done" }));
            Assert.Contains(s.B, Items(await Caller.JsonAsync(await viewer.GetAsync($"{alerts}/{s.A}/suggestions"))).Select(i => i.Other));
            Assert.Empty((await Caller.JsonAsync(await viewer.GetAsync($"{incidents}/{one}/suggestions"))).GetProperty("items").EnumerateArray());

            // A joins B's incident; reopening the first would put A in two unresolved incidents, so it's refused, audited, until A leaves one.
            var joined = (await Caller.JsonAsync(await @operator.PostAsync($"{alerts}/{s.A}/suggestions/{s.B}/accept", new { }))).GetProperty("incidentId").GetGuid();
            // Without a comment it's the missing comment that's reported, before anything is checked or audited.
            Assert.Equal(HttpStatusCode.BadRequest, (await @operator.PostAsync($"{incidents}/{one}/transitions", new { to = "investigating" })).StatusCode);
            var reopen = await @operator.PostAsync($"{incidents}/{one}/transitions", new { to = "investigating", comment = "again" });
            Assert.Equal(HttpStatusCode.Conflict, reopen.StatusCode);
            Assert.Contains("joined another unresolved incident", await reopen.Content.ReadAsStringAsync(Ct));
            Assert.Equal(HttpStatusCode.NoContent, (await @operator.SendAsync(HttpMethod.Delete, $"{incidents}/{joined}/alerts/{s.A}")).StatusCode);
            await Caller.JsonAsync(await @operator.PostAsync($"{incidents}/{one}/transitions", new { to = "investigating", comment = "again" }));
            await using var db = database.Context();
            Assert.Equal("alert_in_another_incident", (await db.AuditRecords.AsNoTracking().SingleAsync(x => x.Operation == "incident.transition" && x.Outcome == AuditOutcome.Failed, Ct)).Metadata["reason"]);
        }

        /// <summary>A retry sent while the first is still running gets the first's answer: opening an incident (with or without alerts) and moving one.</summary>
        [Fact]
        public async Task Retries_with_one_key_at_the_same_time_get_one_answer_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var s = await SeedAsync(database);
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (_, @operator, _, tenant) = await Caller.AllAsync(client, database);
            var incidents = $"/api/v1/tenants/{tenant}/incidents";

            foreach (var (key, body) in new[] { ("open-empty", (object)new { title = "nothing yet" }), ("open-a", new { title = "orders", alertIds = new[] { s.A } }) })
            {
                var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => @operator.PostAsync(incidents, body, key: key)));
                Assert.All(answers, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
                Assert.Single((await Task.WhenAll(answers.Select(Caller.JsonAsync))).Select(j => j.GetProperty("id").GetGuid()).Distinct());
            }
            var id = (await Caller.JsonAsync(await @operator.PostAsync(incidents, new { title = "orders" }, key: "open-a2"))).GetProperty("id").GetGuid();
            var moves = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => @operator.PostAsync($"{incidents}/{id}/transitions", new { to = "resolved", comment = "done" }, key: "resolve-1")));
            Assert.All(moves, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            await using var db = database.Context();
            Assert.Equal(3, await db.Incidents.CountAsync(i => i.TenantId == tenant, Ct));
            Assert.Equal(1, await db.IncidentEvents.CountAsync(e => e.IncidentId == id && e.Type == IncidentEventTypes.State, Ct));
        }

        /// <summary>Pairs that can't be suggested (decided, or the focus itself) don't use up the candidates read: B is still found behind 500 dismissed alerts.</summary>
        [Fact]
        public async Task Decided_pairs_dont_crowd_out_the_others_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var s = await SeedAsync(database);
            await using (var db = database.Context())
            {
                var a = await db.Alerts.SingleAsync(x => x.Id == s.A, Ct);
                for (var i = 0; i < CorrelationSuggestions.MaxCandidates; i++)
                {
                    var noise = new Alert
                    {
                        TenantId = a.TenantId, RuleId = a.RuleId, DedupKey = $"noise-{i}", SubjectKey = $"service:noise{i}@production", SubjectType = HealthSubject.Service,
                        EnvironmentId = a.EnvironmentId, Severity = AlertSeverity.Warning, State = AlertState.Firing, BreachStartedAt = a.BreachStartedAt, FiredAt = a.FiredAt, LastEvaluatedAt = a.LastEvaluatedAt,
                    };
                    var (first, second) = CorrelationDecision.Pair(a.Id, noise.Id);
                    db.Alerts.Add(noise);
                    db.CorrelationDecisions.Add(new CorrelationDecision { TenantId = a.TenantId, FirstAlertId = first, SecondAlertId = second, Decision = CorrelationDecisionKind.Dismissed, WeightsVersion = "test", Reason = "noise", DecidedBy = "erin", DecidedAt = DateTimeOffset.UtcNow });
                }
                await db.SaveChangesAsync(Ct);
            }
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, _, _, tenant) = await Caller.AllAsync(client, database);
            Assert.Equal([s.B, s.H, s.F], Items(await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/alerts/{s.A}/suggestions"))).Select(i => i.Other));
        }

        [Fact]
        public async Task Dependencies_traces_no_longer_show_are_no_evidence_Async()
        {
            LocalStack.RequireAvailable();
            await using var database = await LocalStack.CreateDatabaseAsync();
            var s = await SeedAsync(database);
            await using (var db = database.Context())
            {
                await db.Dependencies.ExecuteUpdateAsync(u => u.SetProperty(d => d.LastSeen, DateTimeOffset.UtcNow.AddDays(-8)), Ct);
            }
            await using var factory = new ApiFactory(database);
            using var client = factory.CreateClient();
            var (viewer, _, _, tenant) = await Caller.AllAsync(client, database);
            var best = (await Caller.JsonAsync(await viewer.GetAsync($"/api/v1/tenants/{tenant}/alerts/{s.A}/suggestions"))).GetProperty("items")[0];
            Assert.Equal(s.B, best.GetProperty("other").GetProperty("id").GetGuid());
            // Host, payments' deployment and timing remain; the direct dependency and the shared unhealthy ledger don't.
            Assert.Equal(["same-host", "recent-change", "fired-together"], best.GetProperty("evidence").EnumerateArray().Select(x => x.GetProperty("code").GetString()));
            Assert.Equal(0.8, best.GetProperty("score").GetDouble());
        }
    }
}
