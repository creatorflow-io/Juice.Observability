# Phase 8 plan: approved manual actions and verified remediation

**Status:** done 9 November; gate accepted by the owner 2026-11-12. The owner's decisions are marked in the first table; the rest are mine, for review.

**Roadmap gate:**
- failure, timeout, duplicate, stale-evidence and approval cases are tested;
- a resource is not marked recovered until independent health evidence confirms it.

## Decisions

| # | Decision | By |
|---|---|---|
| P1 | **Approval is configurable by risk.** Every action has a risk: `Low`, `Medium` or `High`. The platform's `Actions:Approval` says what each risk needs: `None` (runs on request), `Admin` (an admin requests it, or an admin approves an operator's request), or `SecondPerson` (a different admin approves, never the requester). A per-action override is possible. Defaults: Low `None`, Medium `Admin`, High `SecondPerson`. | Owner |
| P2 | **Two built-in remediation actions:** `service.restart` and `files.cleanup`. Anything else comes from customers' custom actions. | Owner |
| P3 | **Diagnostic bundles live in PostgreSQL:** at most 5 MB each, deleted after 14 days. | Owner |
| P4 | **Risk of the actions.** The diagnostics are Low. `files.cleanup` is Medium. `service.restart` is High. `agent.update` is Medium, so an admin still updates in one step. A custom action declares its own risk: Low by default if read-only, Medium if changing, and never Low if it changes the host. A built-in's risk is the platform's own: an agent's report can't lower it. | Mine |
| P5 | **Approvals expire after 30 minutes,** and a command is signed only once its request is approved. The signed envelope carries the request and approver as its approval reference. A host's allowlist can demand one per action (`RequireApproval`), so a platform that skipped approval is still refused on the host. | Mine |
| P6 | **One open request per agent, action and target.** A second identical one is refused as a duplicate while the first is pending, approved or running. | Mine |
| P7 | **`service.restart` uses fixed system interfaces, never a shell or a command string.** On Linux it is systemd over its D-Bus API, or `systemctl` at its fixed path with the unit as a separate argument. On Windows it is the Service Control Manager. The service must be one the host lists; the command names it, nothing else. | Mine |
| P8 | **`files.cleanup` is bounded on the host.** Details below. | Mine |
| P9 | **Preconditions are checked when a request is approved and again when it's signed.** For example: the target resource is still unhealthy, or the alert that prompted it is still firing. Evidence older than 10 minutes counts as stale, and the request is refused as `stale_evidence`. | Mine |
| P10 | **Recovery is verified, never assumed.** After a remediation succeeds on the host, the request stays `Verifying` until the health engine reports the resource healthy from telemetry. That evidence must be newer than the action. It ends `Verified`, or `NotVerified` once the action's verification window has passed (10 minutes by default). The agent saying "done" is not recovery. | Mine |

### `files.cleanup`

For services that write logs or files without rotating them. The host's allowlist names each folder it may clean:

```json
"files.cleanup": {
  "Folders": [
    { "Path": "/var/log/legacy-app", "Pattern": "*.log", "KeepHours": 72, "Recursive": false }
  ]
}
```

- **What a command names:** one of those folders, and optionally a longer keep time. Never a shorter one: the host's `KeepHours` is the minimum.
- **What gets deleted:** only regular files matching the pattern and last written more than the keep time ago.
- **What it never touches:** links, directories, and anything outside the folder. Subfolders only with `Recursive`.
- **Bounded runs:** at most 10,000 files per run, under the action's timeout. What's left is reported, and another run continues.
- **Dry run:** `dryRun: true` reports what would go, without deleting anything. A dry run is Low risk; a real one is Medium.
- **The result:** files and bytes removed, the oldest kept, and whether the run stopped at its limit.

## Milestones

1. **Requests and approvals:**
   - risk on every action;
   - the `Actions:Approval` policy;
   - requesting, approving, rejecting and expiring, all audited;
   - approval references checked on the host;
   - the duplicate rule;
   - a Console flow (request, see what's waiting, approve or reject).
2. **Remediation and verification:**
   - `service.restart` and `files.cleanup`;
   - preconditions and stale evidence;
   - recovery verified from health;
   - timeouts and failures.
3. **Diagnostic bundles:** a built-in read-only `diagnostics.bundle` gathers bounded host facts and excerpts of the log files the host lists. It's stored capped, downloadable, and deleted after 14 days.

Each milestone ends with its tests, a mutation check, the local-stack demos and an acceptance record.

## Milestone 1: done, 9 November

- **API:** `POST agents/{id}/action-requests` (operators and admins, with a reason). Then `GET action-requests` (`state`, `agentId`), and
  `GET`, `approval`, `rejection` and `cancellation` on `action-requests/{id}`.
- **Direct commands:** `POST agents/{id}/commands` still issues what needs no one else's approval: policy `None`, or `Admin` asked by an
  admin, which is recorded as a request that admin approved. Anything else gets `409 Approval required`. Before, an operator's update got
  `403`.
- **Agent report:** every action carries its risk, and `GET agents/{id}/actions` also says what approval it needs.
- **The host:** `RequireApproval` in an action's allowlist entry refuses a command with no approval reference (`approval_required`).
- **Console:** an action that needs approval asks for a reason and becomes a request. The agent page lists its requests, with Cancel. A
  new **Approvals** page shows what's waiting and what was decided lately.
- **Local stack:** a second demo admin, `adam`, for second-person approvals.

| Gate case | Evidence |
|---|---|
| Approval by policy: Low runs, Medium needs an admin, a second-person action can't be approved by its requester or an operator, and approving twice is refused | `AgentFlowTests.Actions_run_as_their_risks_approval_policy_says_and_a_second_person_approves_their_own_kind`, with a real agent |
| The host refuses a command without approval where it requires one | `AgentCustomActionTests.A_host_that_requires_approval_refuses_a_command_without_an_approval_reference`; the flow test's approved command runs on a host that requires it |
| Duplicate | The same request again while one is open: `409`, audited |
| Stale approval | `An_undecided_request_expires_and_cant_be_approved_after`: an expired request can't be approved, isn't pending, and no command was ever signed |
| Every step audited | `action.request.create`, `approve` (the refused own approval as `Denied`), `reject` and `cancel` |
| Through the browser | `console/e2e/approvals.mjs`, 9 November: an operator's update waited, an admin couldn't reject it without a reason, approved it, and it ran. A second request was rejected. `agents-console.mjs` passes on both addresses. |
| Mutation check | 7 of 7 caught: the second-person rule, a command skipping its policy, duplicates, expiry, an operator approving, the host's `RequireApproval`, and per-action overrides |

Not yet: preconditions, stale health evidence and verified recovery (milestone 2).

### Added after milestone 1: telling approvers, 9 November

- **The menu count:** the Approvals entry shows how many requests wait for the signed-in user (`GET action-requests/waiting`): none for an
  operator, and not an admin's own second-person request. It refreshes every 30 seconds on any page, and at once after a page makes or
  decides a request. juice-layout's menu has no badge field, so the library adds one to that entry; a `badge` on juice-layout's `Menu`
  would be the cleaner home.
- **Messages:** the endpoints in `Actions:NotifyEndpoints` are told when a request waits, and when it's approved, rejected, cancelled or
  expires. The deliveries are written with the change, then sent, signed and retried like an alert's.
- **Expiry:** a sweeper closes requests past their deadline, audits each as `action.request.expire`, and notifies it.

| Case | Evidence |
|---|---|
| Notices for every outcome, signed, to a real receiver; none for a request issued at once; the expiry swept and audited; the count per caller | `AgentFlowTests.Approvers_are_told_when_a_request_waits_and_how_it_ends` |
| Chat and email text, escaped against mentions, links and mail headers | `NotificationLogicTests` (four request-notice tests) |
| Through the browser | `approvals.mjs`: the admin sees the count, the operator doesn't, and it goes once approved |
| Mutation check | 3 of 3 caught: no notice when a request waits, an admin's own second-person request counted, the sweeper not closing |

## Milestone 2: done, 9 November

- **On the host:** `service.restart` (High) and `files.cleanup` (Medium; a dry run is Low) act only on what the allowlist lists: `Services`
  by exact name, and `Folders` with their pattern and minimum keep time. An invalid entry stops the agent (exit code 78).
  - A restart uses systemctl at `/usr/bin/systemctl` or `/bin/systemctl`, with the unit after `--` as its own argument, or the Windows
    Service Control Manager. That is the one process the agent may start: `AgentVerifierTests` holds `Systemctl.cs` to one start, no
    shell, the fixed paths, and arguments added one by one. It was argued for by P7; D-Bus would have needed a new library and still no
    systemd in a container.
  - A cleanup skips links and anything outside its folder, keeps at least the host's keep time, stops at 10,000 files, and reports what
    it removed and kept.
- **Preconditions (P9):** a request can name the alert that prompted it, a health target, or both (the target defaults to the alert's
  subject). They're optional: an action without them runs on approval alone. With them, the request is refused when created and again
  when approved if the alert isn't firing or the target is healthy (`precondition_failed`), or the evidence is older than
  `Actions:StaleEvidence` (`stale_evidence`). One whose target recovered before approval is closed as `Cancelled` by `platform`.
- **Verified recovery (P10):** an aimed request reads `Verifying` once issued. The sweeper calls it `Verified` only when the target's
  health is `Healthy` from telemetry newer than the action's end. It's `NotVerified` when the action fails, or the window
  (`Actions:VerificationWindow`) passes, with the reason. Both are audited (`action.request.verify`) and notified.
- **Console:** the actions under their own names, a host-health checkbox for a changing action that needs approval, the verification on
  the agent's requests and the Approvals page.

| Gate case | Evidence |
|---|---|
| Stale evidence | `AgentFlowTests.A_remediation_needs_fresh_evidence_and_counts_as_recovered_only_by_newer_health`: health 20 minutes old is refused, audited `stale_evidence` |
| Precondition | the same test: a healthy target is refused; one that recovers before approval is closed by the platform and never runs |
| Recovered only by independent evidence | the same test: after the agent's success the request stays `Verifying` until newer healthy evidence arrives; unhealthy past the window, `NotVerified`. `RemediationTests.Recovery_is_verified_only_by_healthy_evidence_newer_than_the_action` covers the rule case by case |
| Failure and timeout | a failed action is `NotVerified` with its reason (unit test); the browser check's restart fails with `systemctl not found`; a restart times out after 90 seconds and a cleanup after 60 |
| Bounded cleanup | `RemediationTests`: only old matching files go, a longer keep time keeps more, `Recursive` reaches subfolders, a dry run deletes nothing, the run stops at its limit, a link (or a junction on Windows) is never followed |
| The host decides | `RemediationTests`: unlisted services and folders, a shorter keep time, and invalid allowlist entries are refused; service names can't be options, paths or shell |
| Through the browser | `console/e2e/remediation.mjs`, 9 November: a dry run ran without approval and deleted nothing; the real cleanup waited for an admin and deleted only the old file; a restart aimed at the healthy host was refused; unaimed, it was approved and failed without systemd |
| Mutation check | 11 of 11 caught: old evidence counted as recovery, stale and healthy targets accepted, no recheck at approval, an approved remediation not watched, any service allowed, a shorter keep time allowed, links followed, a dry run deleting, a dry run keeping the action's risk, the service name pattern loosened |

The Console aims a request only at the agent's own host; aiming at a service or an alert is through the API for now.

## Milestone 3: done, 9 November

- **On the host:** `diagnostics.bundle` (read-only, Low) zips the host summary and disk usage (only as far as the host lets `host.summary`
  and `disk.usage` read, through the agent's own diagnostics), the agent's version and allowlist, and the
  last lines (200 by default, 10 to 5,000) of each log in the allowlist's `LogFiles` (up to 20, by exact path). A log is read from at
  most its last 512 KB and never through a link. Values that look like credentials are redacted. The zip stays under 5 MB; a log that
  doesn't fit is left out and named.
- **Upload:** `POST /agent/v1/commands/{id}/bundle`, before the result: only for a running `diagnostics.bundle` command of the agent's
  own, a zip, at most 5 MB whether or not it declares its length, once per command (the same bytes again are the same bundle). Audited
  as `agent.bundle.upload`.
- **Kept (P3):** in PostgreSQL (`agent_bundles`), deleted after `Actions:BundleRetention` (14 days) by the request sweeper.
- **Download:** `GET agents/{id}/bundles` lists them for anyone who sees the agent; `GET agents/{id}/bundles/{bundleId}` returns the zip
  to operators and admins only, audited as `agent.bundle.download` (refusals too).
- **Console:** the command's answer shows the size, entries, redactions and what was left out, with **Download**.

| Case | Evidence |
|---|---|
| Bounded content | `DiagnosticBundleTests`: only listed logs, from their end, a cut first line dropped; credentials redacted case by case; under 5 MB with 14 incompressible logs, the rest left out and named; invalid `LogFiles` refused |
| End to end | `AgentFlowTests.A_bundle_is_made_on_the_host_downloaded_by_operators_only_and_dropped_after_its_retention`: a real agent's bundle, its hash, the redacted log, a viewer refused, downloads audited, deleted past retention |
| Upload refusals | `AgentFlowTests.A_bundle_upload_is_bounded_and_belongs_to_one_running_command`: another action's command, not a zip, too large (declared or chunked), a second different bundle, a finished command |
| Through the browser | `console/e2e/bundle.mjs`, 9 November: an operator's bundle ran without approval, said syslog was missing and one value was redacted; the downloaded zip had the password redacted; a viewer couldn't download it |
| Review before commit | Five fixes, each with a test: the bundle no longer reads past `host.summary` and `disk.usage`'s bounds; it reads through the agent's shared diagnostics, so a stuck read isn't stacked; concurrent uploads of one command are serialized (one bundle, the same id to all); a download error shows under its own bundle only; redaction also covers `AccountKey`, `SharedAccessKey`, a bare `Bearer` token and a URL's password. 7 of 7 mutants of those fixes caught, after two tests were strengthened |
| Mutation check | 10 of 10 caught: no redaction, no size budget, the whole log read, unlisted logs included, upload size unchecked while reading (caught once the test sent a chunked body), any action's upload, an upload after the command finished, a second bundle replacing the first, viewers downloading, bundles kept forever |

Not covered by a test: a log file that is a symbolic link. The agent skips it, but this Windows machine can't create file links without
Developer Mode, and a junction is only for folders.
