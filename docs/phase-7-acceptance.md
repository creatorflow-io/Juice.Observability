# Phase 7 acceptance: Juice Agent enrollment and read-only diagnostics

**Status:** milestone 1 of 2 built and verified. **Not yet reviewed by the owner.** Milestone 2, signed agent updates and rollback, isn't started.

## The gate

| Roadmap requirement | How it's met | Evidence |
|---|---|---|
| An invalid signature is rejected | The agent verifies ECDSA P-256 over the exact bytes it received, with the key it was given at enrollment, before reading them. Altered bytes, another key, or another key's id are refused | `AgentVerifierTests.An_invalid_signature_is_refused`, `…signed_by_another_key…` |
| A wrong target is rejected | The envelope names the agent and the tenant; the agent refuses any other. The platform hands an agent only its own commands, and only that agent can answer them | `A_command_for_another_agent_or_tenant_is_refused`, `AgentFlowTests.An_agent_gets_only_its_own_commands_and_cant_answer_another_agents` |
| An expired or replayed command is rejected | Issued and expiry times, with 2 minutes' clock skew, and at most 10 minutes' lifetime. Each nonce is remembered on disk until it could no longer be valid, so a replay is refused across restarts; the same command redelivered gets its first answer without running again | `A_command_outside_its_time_window_is_refused`, `A_replayed_command_is_refused…`, `The_agent_remembers_nonces_across_a_restart…` |
| A disallowed action is rejected | The local allowlist lists what this host permits; anything not listed is refused. An empty allowlist permits nothing | `A_disallowed_action_is_refused_though_the_platform_signed_it`, and on the local stack (`console/e2e/agent.mjs`) |
| Out-of-range parameters are rejected | Each action's parameters are typed and bounded, unknown members refused, checked by the platform before it signs and again by the agent. The local allowlist can bound them further | `Out_of_range_or_unknown_parameters_are_refused`, `Valid_parameters_outside_this_hosts_bounds_are_refused` |
| The local allowlist wins over a valid platform signature | It is checked after the signature, and a validly signed command it doesn't permit is refused, with the reason reported to the platform | `A_disallowed_action_is_refused_though_the_platform_signed_it`; end to end, `An_agent_enrolls_runs_what_its_allowlist_permits_refuses_the_rest…` |
| No shell execution interface exists | The three actions read the host through .NET APIs and `/proc`. Nothing in the agent or its contracts can start a process, run a shell or load code a command names, and a test fails if that changes | `AgentVerifierTests.The_agent_has_no_way_to_run_a_command` |

Every check above was also broken on purpose, one at a time, and a test failed each time: 15 of 15, on both the agent's and the platform's side.

## Deliverables of milestone 1

- **Enrollment:** an admin creates a one-time token (shown once, stored as a hash, 5 minutes to 24 hours). The agent sends it with a CSR for a key it generates and keeps; the platform's agent CA issues a 7-day client certificate naming the agent. The agent renews it when a third is left, and the old certificate keeps working only until the new one is first used.
- **Revocation:** immediate. Every agent call is checked against the agent's record, so the next one is refused; waiting commands are cancelled.
- **Identity and resource association:** the agent is linked to the host discovery found with the same name.
- **Transport:** the agent only connects out, to the platform's agent listener over mTLS, and long-polls for commands. It trusts only the configured CA for the platform, not the machine's store. The listener serves nothing else, and the agent API is served nowhere else.
- **Signed commands:** a typed envelope with target agent and tenant, issue and expiry times, nonce, idempotency key and action, signed by a command key kept apart from the CA.
- **Diagnostics:** `host.summary`, `disk.usage` and `tcp.reachability`, each with a timeout.
- **Audit:** token creation, enrollment, renewal, revocation, every command issued and every result, refusals included.

## Decisions to check

- **Operators can run diagnostics; only admins enroll and revoke.** Diagnostics are read-only, and checking a host during an incident shouldn't need an admin.
- **The platform checks parameters too**, though the agent is what enforces them: a command the agent would refuse is better refused before it's signed.
- **A redelivered command is answered from memory,** not run again, and a new command with an idempotency key already done gets that result.
- **TCP reachability needs targets on the host's allowlist.** Otherwise an agent could be used to scan the customer's network from inside.
- **`disk.usage` with no path** reports every filesystem, so a host that bounds paths refuses it.
- **Results are kept indefinitely,** like incidents; no retention policy yet.

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

- **No Console page yet.** Agents are used through the API; the focused console is Phase 10.
- **Rotating the command key or the agent CA needs agents to enroll again.** Rotation without it comes with signed updates in milestone 2.
- **A command issued on another replica than the agent's poll** reaches it within 5 seconds, at the poll's next look; on the same replica, at once.
- **The enrollment guard counts per replica,** so with several replicas an address gets 10 failures a minute from each.
- **No approval step.** The envelope has a field for an approval reference, unused until Phase 8's actions need approval.
- **The agent runs as a plain process or container.** No Windows service or systemd unit is provided yet.
- **Diagnostics from a container see the container,** except what reads the host's `/proc` and mounts under `Agent:HostRoot`.
