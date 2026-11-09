# Phase 8 plan: approved manual actions and verified remediation

**Status:** in progress. Owner's decisions from 9 November are in the first table; the rest are mine, for review.

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

Not yet: preconditions, stale health evidence and verified recovery (milestone 2), and an approval's notifications. Nobody is told a request is
waiting, except through the Approvals page.
