# Phase 6 acceptance: broader notifications and incident workflow

**Status:** verified, waiting for the owner's review. All 361 tests pass, both browser journeys pass on an image with the final code, and all 21 mutation checks were caught. **Not yet reviewed by the owner.** Slack, Teams and a TLS mail server have not been tried for real: see Limits.

## How it was checked

| Check | Result |
|---|---|
| Test suite | 361 of 361 |
| Notifications journey (`console/e2e/notifications.mjs`) against a rebuilt image | No problems. It received 3 signed, 2 Slack, 2 Teams and 2 mail notifications, and a channel answering 503 showed its retries and reason |
| Main journey (`console/e2e/journey.mjs`) on the same image | No problems |
| Mutation checks: 21 deliberate breaks, one at a time, against the notification, incident and webhook tests | 21 of 21 caught |

The mutation checks cover silences, closing alerts, secret references, the per-tenant allowlist, Slack escaping, grouping races, escalation, file ownership, roles, audit, masking, chat hosts, routing and addresses in the audit. They show that a test notices each of those breaks, not that the behaviour is right everywhere.

## The gate

| Roadmap requirement | How it's met | Evidence |
|---|---|---|
| Delivery failure and retry are visible | `GET /alerts/{id}/notifications` and the alert page list every delivery with status, attempts and last error. `GET /webhooks` counts failures in the last 24 hours and what's waiting, and the Notifications page has each channel's history | `One_alerts_notifications_show_failure_retry_silence_and_escalation`, journey step `failures-visible` with a receiver answering 503 |
| Silence behaviour is visible | The alert page says a silence is holding notifications, and why. A held delivery shows `Suppressed` with its silence, and the Silences page counts what each holds | `A_silence_holds_the_notification_not_the_alert...`, journey step `silence-holds-escalation` |
| Incident transitions are auditable | Each one writes an audit record and an event on the incident in one transaction, with actor and correlation id. Refused ones are audited too | `Every_transition_is_recorded_and_audited_and_changes_no_alert`, journey step `incident` |
| Escalation stops on resolution or acknowledgement according to policy | Resolving always stops it. Acknowledging stops it if the policy's `stopOnAcknowledge` says so, and `alert_escalations.stop_reason` records why | `Resolving_stops_an_escalation`, `Acknowledging_stops_an_escalation_only_if_the_policy_says_so`, journey step `acknowledge-stops-escalation` |

## Deliverables

- **Create and update APIs** for rules, endpoints and the allowlist, admin only and audited, with an `If-Match` check on rules. The deployment's files still work; each row belongs to one side (see the [runbook](runbook.md)).
- **Channels:** signed webhook, Slack, Teams and email.
- **Routing:** an endpoint chooses events, rules, environments and minimum severity.
- **Grouping:** `groupWindowSeconds` waits and sends what gathered as one notification.
- **Deduplication:** one delivery per transition, endpoint and escalation step, by a unique index.
- **Silences and maintenance windows:** scoped, always with a reason and an end, optionally weekly. They hold notifications and never change an alert.
- **Escalation:** ordered steps by minutes after firing.
- **Delivery history** per channel and per alert.
- **Incidents:** Open, Investigating, Mitigated, Resolved, reopening, notes, alerts, and their own record of who did what.
- **Console:** Rules can be changed; new Notifications, Silences and Incidents pages; the alert page shows notifications and links to silence it or open an incident.

## Decisions to check

These weren't in the roadmap, so they're mine to justify and yours to overrule.

- **What a silence held is released when it ends, if it still matters.** Still firing: sent, so a real outage after maintenance gets noticed. Resolved meanwhile: dropped, unless the receiver was told it was firing, in which case it hears the resolve.
- **An acknowledged alert's held firing is dropped** when the silence ends, since someone is already on it. A held escalation step follows its policy's `stopOnAcknowledge`.
- **Escalation steps keep their spacing when late.** After a silence or downtime, the overdue step goes out and the next waits the policy's gap from then, so the later people still get the time the policy gives the earlier ones. Before 7 November, every overdue step went out about 2 seconds apart.
- **Operators can silence and open incidents; only admins change rules, channels, the allowlist and escalation.** Stopping noise during an outage shouldn't need an admin.
- **Disabling a rule, or changing its template or scope, resolves its open alerts** with `rule_disabled` or `rule_changed`. Nothing would watch them any more, and they'd fire and escalate forever. This also fixes alerts stranded by rules removed from the file in earlier versions. A file emptied by mistake therefore resolves its alerts and sends those notifications.
- **A secret reference set through the API is restricted** to `env:JUICE_WEBHOOK_*`, or files directly in `Webhooks:ApiSecretDirectory`. Otherwise an admin could name any variable or file on the host as a signing key.
- **Slack and Teams URLs live behind a secret reference**, not in the database, since the URL is the credential. They may only post to those services' hosts, plus `Webhooks:AdditionalChatHosts`.
- **Text from telemetry is made harmless per channel.** A service named `<!channel>` can't page a Slack channel, and `[x](http://evil)` can't become a Teams link.
- **Incidents and alerts don't drive each other.** Resolving an incident changes no alert. An alert is in one unresolved incident at most.
- **Rules and endpoints are disabled, never deleted,** since alerts and deliveries keep them.

## Fixed along the way

- **Notifications now hear of alerts through the Juice outbox** (7 November). Before, the rule code opened its own transaction and wrote the channel deliveries itself. Now every command that changes an alert runs under the framework's `TransactionBehavior`, which commits the alert with an `AlertTransitionedIntegrationEvent` in the outbox. The notification module plans the deliveries from that event, once per transition. See [architecture](architecture.md#data-flow) and the [runbook](runbook.md#webhook-deliveries).

- **The allowlist was read across tenants** when connecting. It's now per tenant, with one HTTP client per tenant so a pooled connection can't skip the check.
- **An incident's record could show "Alert added" before "Opened".** Both had the same time and sorted by an id that's random within a millisecond, which also made one test fail now and then. They're now a microsecond apart. The test then passed in 8 repeat runs and the full suite; the cause is reasoned from the code, not observed directly.

## Limits

- **Slack, Teams and TLS mail haven't met the real thing.** The formats follow those services' documentation and reached local receivers only, and the mail test server has no TLS. Try each with a real channel before relying on it.
- **Implicit TLS on port 465 isn't supported:** `System.Net.Mail` does STARTTLS only.
- **A hand retry is one more attempt.** If it fails again for a transient reason, the delivery is `Failed` again rather than going back to the backoff schedule.

Resolved on 8 November, each with a test that fails without the fix:
- **A test send:** `POST /webhooks/{id}/test` and the Console's Test button send a `webhook.test` notification through the same checks, signing and formatting as an alert's. They work on a disabled channel too, and are audited (`WebhookOperationsTests`).
- **A failed delivery retried by an admin:** `POST /webhooks/{id}/deliveries/{deliveryId}/retry` and the Console's Retry button, audited, in place of the runbook's SQL.
- **An escalation step whose channels are all disabled** no longer counts as sent:
  - It's audited (`escalation.step.unreachable`) and logged as an error, and the next step is due at once.
  - If it was the last step, the escalation stops as `unreachable`.
  - Evidence: `NotificationFlowTests.A_step_that_reaches_nobody_isnt_counted_and_the_next_one_goes_at_once`.
- **The race between escalating and acknowledging is tested at the overlap.** An acknowledgement held mid-transaction makes the escalation wait on the alert's row lock, then stop without notifying (`…An_escalation_that_meets_an_acknowledgement_in_flight_waits_for_it_and_stops`).
- **Removing an allowlist entry takes effect at the next send.** The client is replaced when the tenant's allowlist changes, instead of keeping a pooled connection for up to a minute (`WebhookOperationsTests.Removing_an_allowlist_entry_stops_the_next_send_though_a_connection_is_open`).
- **Incidents have no assignee, don't notify anyone, and aren't on the Timeline.**
- **The Console shows change buttons to everyone;** the API refuses and the page says which role is needed.
- **Upgrading writes one `webhook.update` audit record and change event** per file-managed endpoint on first start, since the definition gained fields.
- **Silences, incidents and deliveries are kept indefinitely.** No retention policy exists for them yet.
