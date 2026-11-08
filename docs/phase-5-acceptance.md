# Phase 5 acceptance: change events and initial correlation

**Status:** the gate's checks pass on the local stack. **Not yet reviewed by the owner.**

## The gate

| Roadmap requirement | How it's met | Evidence |
|---|---|---|
| Related timeline is tenant scoped | Every query filters on the tenant; another tenant's change never shows | `ChangeApiTests` with a second tenant's change in the same database |
| Secrets are excluded | Attributes are allowlisted and text is masked before storing; the audit record holds no request text | `ChangeSanitizerTests`, and `ChangeApiTests` checks the stored event and audit for the secret. Journey step `alert-related` |
| Proximity is labelled correlation, not root cause | `relation: correlation` and the note on every response and on the alert page | `CorrelationRankerTests`, journey step `alert-related` |

## Deliverables

- **Change events:** deployment, version and configuration, with safe summaries, actor and source, mapped to a service or resource.
- **Timeline:** `GET /timeline` and the Console's Timeline page join changes, health and alerts; a service's telemetry is drawn above them over the same window.
- **Ranking:** `GET /alerts/{id}/related` and the alert page, with the reason for every point.

## Limits

- **Masking catches common shapes, not every secret.** An unusual secret in free text can get through, so the docs ask for a summary, not a command or a log.
- **Version changes need `service.version`** in the telemetry, and a new instance to announce them. A service that doesn't report it only has what a pipeline reports.
- **Direct dependencies only.** A change two hops away isn't listed.
- **Ranking isn't validated against real incidents.** The points are reasoned, not tuned: there is no incident history to tune them on.
- **A service must be discovered** before a pipeline can report a change to it, so a first deployment gets `422` until telemetry arrives.
- **Hosts on one database, at different versions:** resolved on 8 November.
  - Before, an older host could hold the shared worker lease and record no version changes. Seen for real: the demo on the released package did this until it was stopped.
  - Now a newer host takes each worker's lease over from an older holder (discovery, health, rules, notifications) and logs a warning. The older host steps down at its next pass.
  - Hosts of the same version never take a lease over from each other, and an older host still takes a free lease, so going back a version keeps the workers running.
  - Evidence: `WorkerLeaseTests.A_newer_version_takes_a_lease_over_from_an_older_holder_and_only_then`.
- **Configuration changes** cover the platform's own rules and webhooks. A customer's own configuration needs a pipeline to report it.
