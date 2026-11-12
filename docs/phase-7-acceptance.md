# Phase 7 acceptance: Juice Agent enrollment and read-only diagnostics

**Status:** accepted by the owner 2026-11-12. Both milestones are built and verified: milestone 1 (enrollment and read-only diagnostics) and milestone 2 (signed updates with rollback, and rotation without re-enrollment).

## The gate

| Roadmap requirement | How it's met | Evidence |
|---|---|---|
| An invalid signature is rejected | The agent verifies ECDSA P-256 over the exact bytes it received, with the key it was given at enrollment, before reading them. Altered bytes, another key, or another key's id are refused | `AgentVerifierTests.An_invalid_signature_is_refused`, `…signed_by_another_key…` |
| A wrong target is rejected | The envelope names the agent and the tenant; the agent refuses any other. The platform hands an agent only its own commands, and only that agent can answer them | `A_command_for_another_agent_or_tenant_is_refused`, `AgentFlowTests.An_agent_gets_only_its_own_commands_and_cant_answer_another_agents` |
| An expired or replayed command is rejected | Issued and expiry times, with 2 minutes' clock skew, and at most 10 minutes' lifetime. Each nonce is remembered on disk until it could no longer be valid, so a replay is refused across restarts; the same command redelivered gets its first answer without running again | `A_command_outside_its_time_window_is_refused`, `A_replayed_command_is_refused…`, `The_agent_remembers_nonces_across_a_restart…` |
| A disallowed action is rejected | The local allowlist lists what this host permits; anything not listed is refused. An empty allowlist permits nothing | `A_disallowed_action_is_refused_though_the_platform_signed_it`, and on the local stack (`console/e2e/agent.mjs`) |
| Out-of-range parameters are rejected | Each action's parameters are typed and bounded, unknown members refused, checked by the platform before it signs and again by the agent. The local allowlist can bound them further | `Out_of_range_or_unknown_parameters_are_refused`, `Valid_parameters_outside_this_hosts_bounds_are_refused` |
| The local allowlist wins over a valid platform signature | It is checked after the signature, and a validly signed command it doesn't permit is refused, with the reason reported to the platform | `A_disallowed_action_is_refused_though_the_platform_signed_it`; end to end, `An_agent_enrolls_runs_what_its_allowlist_permits_refuses_the_rest…` |
| No shell execution interface exists | The three diagnostics read the host through .NET APIs and `/proc`. Nothing in the agent or its contracts can start a process, run a shell or load code a command names, and a test fails if that changes. The launcher starts one thing: the agent version its signed package names, from one file, with no shell and fixed arguments. A test holds it to that | `AgentVerifierTests.The_agent_has_no_way_to_run_a_command`, `…The_launcher_starts_only_the_agent_and_only_in_one_place` |

Every check above was also broken on purpose, one at a time, and a test failed each time: 15 of 15, on both the agent's and the platform's side. Milestone 2's checks were tested the same way: 13 of 13 (see below).

## Deliverables of milestone 1

- **Enrollment:** an admin creates a one-time token (shown once, stored as a hash, 5 minutes to 24 hours). The agent sends it with a CSR for a key it generates and keeps; the platform's agent CA issues a 7-day client certificate naming the agent. The agent renews it when a third is left, and the old certificate keeps working only until the new one is first used.
- **Revocation:** immediate. Every agent call is checked against the agent's record, so the next one is refused; waiting commands are cancelled.
- **Identity and resource association:** the agent is linked to the host discovery found with the same name.
- **Transport:** the agent only connects out, to the platform's agent listener over mTLS, and long-polls for commands. It trusts only the configured CA for the platform, not the machine's store. The listener serves nothing else, and the agent API is served nowhere else.
- **Signed commands:** a typed envelope with target agent and tenant, issue and expiry times, nonce, idempotency key and action, signed by a command key kept apart from the CA.
- **Diagnostics:** `host.summary`, `disk.usage` and `tcp.reachability`, each with a timeout.
- **Audit:** token creation, enrollment, renewal, revocation, every command issued and every result, refusals included.

## Deliverables of milestone 2

- **Signed updates.** A release key signs each agent package. It's kept offline and separate from the CA and the command key. A package is a zip of the published agent with a manifest naming its version and entry point. The signature is the one `openssl dgst -sha256 -sign` makes; `deploy/agent/pack.cs` makes keys and packages.
- **The platform offers only packages a configured release key signed,** and whose manifest names the version in the file name (`GET …/agents/packages`). Only admins issue `agent.update`, naming a version. The platform adds the package's SHA-256, so the agent installs exactly the bytes the platform checked. Packages are served only to enrolled agents, over the agent listener.
- **The agent checks again, on its own terms:**
  - `agent.update` must be in its local allowlist.
  - The bytes must have the hash the signed command names.
  - A release key in the host's own `Agent:ReleaseKeyFiles` must have signed them, so a compromised platform can't push its own build.

  Only then does it unpack into its own directory: no path outside it, at most 10,000 entries and 1 GiB.
- **The launcher** (`juice-agent-launcher`) runs the agent and switches versions only when the agent asks.
  - The new version runs on trial. It's kept once it has polled the platform with its certificate.
  - If it exits, or stays silent for 2 minutes, it's stopped and the version before starts again. A launcher that stops mid-trial also goes back.
  - The outcome becomes the command's result: `Succeeded` with `{ from, to }`, or `Failed` with `rolled_back: …`. Whichever version runs next reports it.
- **Rotation without re-enrollment.** A new command key is announced at the next poll, signed by a key the agent trusts. The agent then trusts the new key and its announcer, and nothing older. The platform accepts certificates from CAs it has rotated away from, and agents get a certificate from the new CA when they renew.
- **Containers are updated by their image.** The image runs the agent without the launcher. `deploy/local` runs it under the launcher so updates can be tried (`console/e2e/agent-update.mjs`).

| Milestone 2 check | Evidence |
|---|---|
| Each of these is refused, with nothing changed that runs: bytes other than the ones the command names, a signature by no trusted release key, a package path outside its directory, a manifest for another version or entry point | `AgentUpdaterTests` |
| Without the launcher or a release key, nothing is downloaded. Without `agent.update` in the allowlist, the command is refused before anything downloads | `AgentUpdaterTests` |
| A version is kept only when it confirms, and only for its own update. It's rolled back when it exits, stays silent, or the launcher stopped during its trial | `AgentLauncherTests` |
| The update's result waits for the launcher's verdict, and a redelivery gets that verdict | `AgentUpdaterTests.The_update_a_version_is_on_trial_for_isnt_answered_until_the_launcher_decides` |
| Only admins update, only to packages the platform verified, with the platform's hash. Packages go only to enrolled agents | `AgentFlowTests.Only_admins_update_agents_and_only_to_signed_packages_the_platform_offers` |
| An announced key is taken only when a trusted key signed exactly that key. Old keys are dropped at the next rotation | `AgentRotationTests` |
| An agent enrolled before a rotation keeps working, learns the new key, runs commands signed with it, and renews from the new CA | `AgentFlowTests.An_agent_enrolled_before_a_rotation_follows_it_without_enrolling_again` |
| On the local stack: an update kept, a broken one rolled back | `console/e2e/agent-update.mjs`, 8 November: `1.1.0-local` → `1.1.0-demo` `Succeeded`, confirmed 26 s after the start (its first long poll). A package with a corrupt assembly, `Failed` `rolled_back: … exited with code 134 during its trial`, back on `1.1.0-demo` within a second. An operator's update `403`. `e2e/agent.mjs` passes on the updated agent |

Thirteen checks were each broken on purpose, one at a time, and a test failed every time:
- the hash check;
- the release signature check;
- path confinement;
- the manifest version check;
- the announcement signature;
- the announced key id;
- accepting previous CAs;
- dropping old keys;
- matching the confirmation to its update;
- rolling back an unwatched trial;
- holding a trial's redelivery;
- the platform's admin-only rule;
- the platform's signature check.

## Decisions to check

- **Operators can run diagnostics; only admins enroll and revoke.** Diagnostics are read-only, and checking a host during an incident shouldn't need an admin.
- **The platform checks parameters too**, though the agent is what enforces them: a command the agent would refuse is better refused before it's signed.
- **A redelivered command is answered from memory,** not run again, and a new command with an idempotency key already done gets that result.
- **TCP reachability needs targets on the host's allowlist.** Otherwise an agent could be used to scan the customer's network from inside.
- **`disk.usage` with no path** reports every filesystem, so a host that bounds paths refuses it.
- **Results are kept indefinitely,** like incidents; no retention policy yet.
- **An update needs three parties to agree,** because it changes the host:
  - an admin on the platform;
  - the host's own allowlist entry;
  - the host's own release key.
- **The trial is 2 minutes, and one successful poll confirms it.** That proves the new version starts, reads its identity and reaches the platform over mTLS. It doesn't run a diagnostic.
- **An update command lives 10 minutes by default,** the maximum, to cover the download, the restart and the trial.
- **After a rotation, an agent trusts two command keys:** the new one and the one that announced it. So commands signed just before the switch still run.

## Fixed along the way

- **The agent CA couldn't issue a certificate for an RSA key,** though RSA of 2048 bits or more is meant to be accepted: .NET refuses to sign across key types without an explicit signature generator. Found by `AgentAuthorityTests`.
- **Ten problems a code review found** (8 November), each fixed with a test that fails without the fix:
  - The agent stopped on any failure it didn't expect, and a command it was running when it stopped ran again at every restart. Now it logs, waits and tries again, and an interrupted command is reported `Failed` (`interrupted`) instead of run again.
  - A result the platform refused for good blocked every result behind it, and the agent never polled again. Now it is dropped, and at most 100 results wait.
  - A crash during renewal could leave a new key beside the old certificate. They're now one file, replaced in one rename. Agents enrolled with two files keep working and move to one at their next renewal.
  - The agent rebuilt its connection and reloaded its certificate on every poll, leaving a key file behind on Windows each time. Now one connection is kept until the certificate changes.
  - `disk.usage` also reported the filesystems above the path, root included: outside what the local allowlist permitted. Now only what's mounted at or under the path, and the filesystem the path is on, named by the path.
  - With agents on, the host listened only on `HTTP_PORTS`, dropping `URLS` and launchSettings addresses. Now it keeps them, as it would without agents.
  - Revoking cancelled waiting commands in a separate commit. Now the revocation, its audit and the cancellations are one save.
  - Anyone who could reach the listener could fill the audit table with failed enrollments. Now an address with 10 failures in a minute gets `429`, and unknown tokens are logged, not audited.
  - Each waiting poll asked the database every second. Now a command issued on the same replica wakes its poll at once, and the poll looks every 5 seconds for the rest.
  - A JSON document was left undisposed when reading commands.
- **Ten more from a second review,** each fixed with a test that fails without the fix, except the size check:
  - A command memory that didn't read left the agent unable to run any command until restarted. Now it's moved aside and the agent starts with an empty one, logging it.
  - `disk.usage` on a hung network mount blocked the agent for good. Now a read that can't be cancelled runs on its own thread and is abandoned at its timeout; while it stays stuck, the next one fails `busy` instead of stacking up.
  - A host name in `URLS` stopped the host from starting; one configured in `Kestrel:Endpoints` got an extra 8080 listener. Now names listen everywhere as Kestrel does, and `Kestrel:Endpoints` is left to Kestrel.
  - The local stack's agent CA and command keys were readable by every user. Now they're `600`, and the platform container runs as their owner (`JUICE_UID`).
  - The enrollment guard could grow without bound when many addresses failed at once. Now it remembers at most 10,000, and a new address waits while it's full.
  - A listener certificate from an intermediate CA was never accepted. Now the listener sends the chain in its certificate file, and agents use what it sends while trusting only their configured CA.
  - An agent on a host discovery doesn't know made every poll search the resources table. Now it looks every 10 minutes.
  - The result size limit counted characters, not bytes; and two small cleanups (a framework helper for tokens, one shared path comparison).

## Limits

- **An agent that's offline through two command key rotations has to enroll again:** the key that could announce the newest one is gone.
- **The launcher stops the agent hard** (`Kill`), not with a signal. Everything the agent writes is replaced by rename, so a stop never leaves half a file. A command running at that moment is reported `interrupted`.
- **No package retention.** Packages stay in `Agents:PackageDirectory` until someone removes them. On a host, the launcher keeps the current version and the one before.
- **A command issued on another replica than the agent's poll** reaches it within 5 seconds, at the poll's next look; on the same replica, at once.
- **No approval step.** The envelope has a field for an approval reference, unused until Phase 8's actions need approval. That now includes
  a customer's custom actions marked `Changing`: until then, any one admin can issue them.
- **A custom action's code is trusted as the customer's own release.** The agent bounds its parameters, its time and its result size, but
  not what the code does: review it as you would any release you sign. One that ignores its cancellation keeps running after the agent
  reports `timeout`.
- **The systemd unit and the Windows service script haven't run on a real host yet.** The launcher has run as a plain process and in a container. The unit file is unverified, and the install script was only parsed, not run, since that needs Administrator.

Resolved on 9 November:
- **The Console has Agents pages.**
  - The list shows each agent's state, version and certificate expiry, and an admin creates the enrollment token there.
  - An agent's page runs each diagnostic and updates, and shows each answer as text rather than JSON.
  - A whole-host disk report shows its fullest 8 filesystems first.
  - Revoking needs a reason and a second click.
  - Checked on the local stack by `console/e2e/agents-console.mjs`, 9 November, with no problems over both `localhost` and the machine's address on plain HTTP. The second run is what found that `crypto.randomUUID()` doesn't exist there: every Console action sending an `Idempotency-Key` (commands, silences, incidents, acknowledging) threw before sending. The key now comes from `crypto.getRandomValues`. The check covers:
    - a viewer's list;
    - an operator's three diagnostics;
    - a refusal by the host's allowlist (`Rejected`, outside local bounds);
    - an operator refused an update and a token, with the page naming the admin role;
    - an admin's token and the update's package list;
    - the revoke confirmation.

  It doesn't revoke unless `REVOKE=1`.
- **A revoked host comes back with a new token, not by clearing files by hand.**
  - The agent records the hash of the token it enrolled with.
  - When the platform refuses it and a different token is in its token file, the agent:
    - sets its identity aside in `revoked/`;
    - enrolls again with a new key, as a new agent.
  - A revoked agent's page has **Bring this host back** to create that token, and links to the new agent once the host has enrolled
    again.
  - Revocation itself stays final.
  - Evidence:
    - `AgentFlowTests.A_revoked_agent_enrolls_again_as_a_new_agent_once_a_new_token_is_in_its_file`, which also checks that the used
      token changes nothing.
    - `console/e2e/agent-reenroll.mjs` on the local stack, 9 November: revoked from the browser, enrolled again 17 s after the token was in
      place.
- **Agents report their actions, and customers can add their own** (Phase 8 work, done early):
  - **Custom actions** are typed `AgentAction<TParameters>` classes in the customer's own agent build, which is signed and installed like
    any release. There are no scripts or commands.
  - **Reports:** the agent reports each action's schema, narrowed to its host's allowlist, and whether the host permits it.
  - **The Console** builds its menu and form from that report.
  - **The platform** signs a custom action only as reported, and keeps changing ones for admins.
  - **Tests:** `AgentCustomActionTests` covers schema, catalog, verification, running, timeout and the report.
    `AgentFlowTests.An_agent_reports_its_custom_actions_and_the_platform_offers_and_checks_them_as_reported` covers the whole path over TLS.
  - **Local stack, 9 November:**
    - `console/e2e/agent-custom.mjs` installed the sample build with `sample.file_info`, ran it, and saw a file the host doesn't list
      refused with `400`.
    - `agents-console.mjs` ran the custom action from the browser, and showed an agent from before reports with its note.
  - **The `Juice.Observability.Agent` package:** the agent project is now packable, so customers can reference it. CI publishes it with
    the other packages.

Resolved on 8 November, each with a test that fails without the fix:
- **The enrollment guard counts across replicas,** in `agent_enrollment_failures`, with one atomic upsert per failure. Each replica's own count is still checked first, so an address it already refuses costs no database work (`AgentListenerTests.Failed_enrollments_add_up_across_replicas`).
- **The agent runs as a service:** `deploy/agent/juice-agent.service` for systemd (`Type=notify`, hardened) and `deploy/agent/install-windows-service.ps1` for Windows, with a virtual account, restart on failure and a private data directory. The launcher integrates with both.
- **An agent with invalid configuration exits with code 78,** not 0. A Windows service's recovery acts only on a failure, so a broken agent used to stop without reporting one (`AgentResilienceTests.An_agent_with_invalid_configuration_ends_with_a_failure_code`).
- **Diagnostics from a container see the container,** except what reads the host's `/proc` and mounts under `Agent:HostRoot`.
