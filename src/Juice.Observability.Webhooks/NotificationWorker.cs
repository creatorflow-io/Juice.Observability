using System.Diagnostics.Metrics;
using Juice.Observability.Data;
using Juice.Observability.Discovery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juice.Observability.Webhooks
{
    /// <summary>
    /// Everything about notifications that isn't sending them, in one replica at a time: releases what a silence held once it ends, and moves
    /// escalations on. Alert state is never changed here. Escalating and acknowledging lock the alert row, so an escalation can't notify after
    /// the acknowledgement that should have stopped it.
    /// </summary>
    public sealed class NotificationWorker(IServiceScopeFactory scopes, IOptions<WebhookOptions> options, IOptions<PlatformOptions> platform, TimeProvider clock,
        ILogger<NotificationWorker> logger) : BackgroundService
    {
        private const int Batch = 500;
        private static readonly Meter Meter = new("Juice.Observability");
        private static readonly Counter<long> Held = Meter.CreateCounter<long>("juice.observability.notifications.held", "{notification}",
            "Notifications a silence held, when the silence ended, by outcome (sent, dropped)");
        private static readonly Counter<long> Escalations = Meter.CreateCounter<long>("juice.observability.notifications.escalations", "{escalation}",
            "Escalation steps and ends, by outcome (notified, unreachable, resolved, acknowledged, policy_disabled, completed)");

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await using var lease = WorkerLease.For(scopes, "notifications", logger);
            using var timer = new PeriodicTimer(options.Value.Interval);
            do
            {
                try
                {
                    if (await lease.TryHoldAsync(stoppingToken))
                    {
                        await RunOnceAsync(stoppingToken);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Notification pass failed; retrying next interval");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }

        public async Task RunOnceAsync(CancellationToken cancellationToken)
        {
            await ReleaseAsync(cancellationToken);
            await EscalateAsync(cancellationToken);
        }

        /// <summary>Silences active now, across tenants; one in its off week is filtered here, since the rule isn't one SQL can say simply.</summary>
        internal static async Task<List<Silence>> ActiveAsync(PlatformDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
            => (await db.Silences.AsNoTracking().Where(s => s.CancelledAt == null && s.StartsAt <= now && (s.RepeatWeekly || s.EndsAt > now)).ToListAsync(cancellationToken))
                .Where(s => SilenceMatcher.IsActive(s, now)).ToList();

        private async Task ReleaseAsync(CancellationToken cancellationToken)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var now = clock.GetUtcNow();
            var active = await ActiveAsync(db, now, cancellationToken);
            var activeIds = active.Select(s => s.Id).ToList();
            var held = await db.WebhookDeliveries.Where(d => d.Status == DeliveryStatus.Suppressed && (d.SuppressedBy == null || !activeIds.Contains(d.SuppressedBy.Value)))
                .OrderBy(d => d.Id).Take(Batch).ToListAsync(cancellationToken);
            if (held.Count == 0)
            {
                return;
            }
            var alertIds = held.Select(d => d.AlertId).Distinct().ToList();
            var alerts = await db.Alerts.AsNoTracking().Where(a => alertIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
            var ruleIds = alerts.Values.Select(a => a.RuleId).Distinct().ToList();
            var rules = await db.Rules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Key, cancellationToken);
            var told = (await db.WebhookDeliveries.AsNoTracking()
                .Where(d => alertIds.Contains(d.AlertId) && d.Event == NotificationPlan.Firing && d.Status != DeliveryStatus.Suppressed && d.Status != DeliveryStatus.Dropped)
                .Select(d => new { d.AlertId, d.EndpointId }).ToListAsync(cancellationToken))
                .Select(d => (d.AlertId, d.EndpointId)).ToHashSet();
            var tenantIds = held.Select(d => d.TenantId).Distinct().ToList();
            var stopsOnAcknowledge = await db.EscalationPolicies.AsNoTracking().Where(p => tenantIds.Contains(p.TenantId))
                .ToDictionaryAsync(p => (p.TenantId, p.Key), p => p.StopOnAcknowledge, cancellationToken);
            foreach (var delivery in held)
            {
                var alert = alerts[delivery.AlertId];
                var context = new AlertContext(rules[alert.RuleId], alert.SubjectKey, alert.Severity);
                // Another silence may cover it too: it stays held, under that one.
                if (SilenceMatcher.Find(active.Where(s => s.TenantId == delivery.TenantId), context, now) is { } other)
                {
                    delivery.SuppressedBy = other.Id;
                    continue;
                }
                // A policy since removed stops on acknowledgement, as a new one does by default.
                var stops = delivery.Event != NotificationPlan.Escalated || PolicyKey(delivery.Payload) is not { } key
                    || stopsOnAcknowledge.GetValueOrDefault((delivery.TenantId, key), true);
                switch (HeldNotification.Decide(delivery.Event, alert.State, told.Contains((delivery.AlertId, delivery.EndpointId)), alert.AcknowledgedAt is not null, stops))
                {
                    case HeldOutcome.Send:
                        delivery.Status = DeliveryStatus.Pending;
                        delivery.NextAttemptAt = now;
                        Held.Add(1, new KeyValuePair<string, object?>("outcome", "sent"));
                        break;
                    default:
                        delivery.Status = DeliveryStatus.Dropped;
                        delivery.LastError = "nothing_to_say_after_silence";
                        Held.Add(1, new KeyValuePair<string, object?>("outcome", "dropped"));
                        break;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        /// <summary>The policy an escalation step's payload names, or null.</summary>
        internal static string? PolicyKey(string payload)
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(payload);
                return json.RootElement.TryGetProperty("escalation", out var escalation) && escalation.ValueKind == System.Text.Json.JsonValueKind.Object
                    && escalation.TryGetProperty("policy", out var policy) ? policy.GetString() : null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        private async Task EscalateAsync(CancellationToken cancellationToken)
        {
            List<Guid> ids;
            await using (var scope = scopes.CreateAsyncScope())
            {
                ids = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().AlertEscalations.AsNoTracking()
                    .Where(e => e.State == EscalationState.Active).OrderBy(e => e.NextAt).Select(e => e.Id).Take(Batch).ToListAsync(cancellationToken);
            }
            foreach (var id in ids)
            {
                await using var scope = scopes.CreateAsyncScope();
                await EscalateOneAsync(scope.ServiceProvider.GetRequiredService<PlatformDbContext>(), id, cancellationToken);
            }
        }

        private async Task EscalateOneAsync(PlatformDbContext db, Guid id, CancellationToken cancellationToken)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var escalation = await db.AlertEscalations.SingleAsync(e => e.Id == id, cancellationToken);
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM alerts WHERE id = {escalation.AlertId} FOR UPDATE", cancellationToken);
            var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.Id == escalation.AlertId, cancellationToken);
            var policy = await db.EscalationPolicies.AsNoTracking().SingleAsync(p => p.Id == escalation.PolicyId, cancellationToken);
            var rule = await db.Rules.AsNoTracking().SingleAsync(r => r.Id == alert.RuleId, cancellationToken);
            var now = clock.GetUtcNow();
            var silences = await ActiveAsync(db, now, cancellationToken);
            var silenced = SilenceMatcher.Find(silences.Where(s => s.TenantId == alert.TenantId), new AlertContext(rule.Key, alert.SubjectKey, alert.Severity), now) is not null;

            var (action, reason) = EscalationDecision.Next(alert.State, alert.AcknowledgedAt is not null, policy.StopOnAcknowledge, policy.Enabled, silenced, escalation.NextAt, now);
            switch (action)
            {
                case EscalationAction.Stop:
                    escalation.State = EscalationState.Stopped;
                    escalation.StopReason = reason;
                    escalation.EndedAt = now;
                    Escalations.Add(1, new KeyValuePair<string, object?>("outcome", reason));
                    break;
                case EscalationAction.Notify:
                    await NotifyAsync(db, escalation, alert, rule, policy, now, cancellationToken);
                    break;
                default:
                    return;   // Not due, or held by a silence: it stays due, and notifies when the silence ends if the alert is still open.
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        private async Task NotifyAsync(PlatformDbContext db, AlertEscalation escalation, Alert alert, Rule rule, EscalationPolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
        {
            // A policy edited down to fewer steps ends here.
            if (escalation.NextStep < policy.Steps.Count)
            {
                var step = policy.Steps[escalation.NextStep];
                var number = escalation.NextStep + 1;
                var transition = await db.AlertTransitions.AsNoTracking().Where(t => t.AlertId == alert.Id && t.ToState == AlertState.Firing)
                    .OrderByDescending(t => t.At).FirstAsync(cancellationToken);
                var endpoints = await db.WebhookEndpoints.AsNoTracking()
                    .Where(e => e.TenantId == alert.TenantId && e.Enabled && step.EndpointKeys.Contains(e.Key)).ToListAsync(cancellationToken);
                foreach (var endpoint in endpoints)
                {
                    var delivery = Guid.CreateVersion7();
                    db.WebhookDeliveries.Add(new WebhookDelivery
                    {
                        Id = delivery, TenantId = alert.TenantId, EndpointId = endpoint.Id, AlertId = alert.Id, TransitionId = transition.Id,
                        Event = NotificationPlan.Escalated, EscalationStep = number, CreatedAt = now, NextAttemptAt = now,
                        Payload = NotificationPlan.Payload(delivery, NotificationPlan.Escalated, platform.Value.TenantKey, rule, alert, transition, (policy.Key, number)),
                    });
                }
                escalation.NextStep = number;
                if (endpoints.Count == 0)
                {
                    // Nobody was told: not a step taken. The next one is due now, rather than after a wait meant for people who never heard.
                    logger.LogError("Escalation {Policy} step {Step} reached nobody: all its endpoints are disabled or gone", policy.Key, number);
                    db.AuditRecords.Add(new AuditRecord
                    {
                        TenantId = alert.TenantId, Actor = "system", Operation = "escalation.step.unreachable", TargetType = "alert", TargetId = alert.Id.ToString(),
                        Outcome = AuditOutcome.Failed,
                        Metadata = new() { ["policy"] = policy.Key, ["step"] = number.ToString(System.Globalization.CultureInfo.InvariantCulture), ["endpoints"] = string.Join(",", step.EndpointKeys) },
                    });
                    Escalations.Add(1, new KeyValuePair<string, object?>("outcome", "unreachable"));
                    if (escalation.NextStep >= policy.Steps.Count)
                    {
                        escalation.State = EscalationState.Stopped;
                        escalation.StopReason = "unreachable";
                        escalation.EndedAt = now;
                    }
                    else
                    {
                        escalation.NextAt = now;
                    }
                    return;
                }
                if (endpoints.Count < step.EndpointKeys.Count)
                {
                    logger.LogWarning("Escalation {Policy} step {Step}: {Missing} of its endpoints are disabled or gone", policy.Key, number, step.EndpointKeys.Count - endpoints.Count);
                }
                Escalations.Add(1, new KeyValuePair<string, object?>("outcome", "notified"));
            }
            if (escalation.NextStep >= policy.Steps.Count)
            {
                escalation.State = EscalationState.Completed;
                escalation.EndedAt = now;
                Escalations.Add(1, new KeyValuePair<string, object?>("outcome", "completed"));
            }
            else
            {
                escalation.NextAt = EscalationDecision.NextAt(policy.Steps, escalation.NextStep, alert.FiredAt ?? now, now);
            }
        }
    }
}
