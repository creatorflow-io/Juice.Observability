# Notifications acceptance: the pipeline is the only one (2.0)

**Status:** M50 slice B. Observability 2.0 removes the legacy webhooks, the shadow run and the migration tool outright: the platform
had no deployments of 1.x, so there was no soak and nothing to migrate. Embedded Juice.Notifications is the only pipeline, and its
endpoints and allowlist are declared in the provisioning file (`Notifications:Provisioning:Path`, [deployment](deployment.md)).

## The evidence the removal rests on

Before anything was deleted, the three-mode parity suite (`Milestone=M46c`: legacy, shadow and new, with bodies compared as proof point 5 says)
ran once on the tip of `release/1.0`:

| Mode (`JUICE_TEST_NOTIFICATIONS_PIPELINE`) | Commit | Result |
|---|---|---|
| `legacy` | `129185b` | 217 passed, 1 skipped (a new-mode-only test), 0 failed |
| `shadow` | `129185b` | 217 passed, 1 skipped, 0 failed |
| `new` | `129185b` | 217 passed, 1 skipped (a legacy-only test), 0 failed |

What it proved lives on without the legacy code: the 1.x formatter is frozen as `test/Juice.Observability.Tests/Legacy/ChannelFormatter.cs`
(with `NotificationPlan.Payload`, which stays in the product), and `BodyParityTests` compares what the declared templates render against it.

## Proof points

Every test is `[Trait("Milestone", "M50")]`; the live ones call `LocalStack.RequireAvailable()` and are skipped without the stack.

| # | Proof point | Test |
|---|---|---|
| 1 | Nothing alert-related is lost silently: any `Webhooks:*` key (`Webhooks:File` included, in any case, with an empty value too) and `Actions:NotifyEndpoints` refuse the start, naming the replacement | `LegacyRemovalTests.Start_AnyWebhooksKey_Refused_NamesReplacement`, `…_TheHostDoesNotStart` |
| 2 | `Notifications:Pipeline` other than `new` refuses the start; `new` (in any case) only warns that it is obsolete | `Start_PipelineLegacyOrShadow_Refused`, `Start_PipelineNewMixedCase_WarnsOnly` |
| 3 | The Console's address is refused unless it is a plain absolute http(s) URL, and the message never repeats it | `ConsoleUrlTests.ConsoleUrl_Refuses_…_ValueNotEchoed`, `ConsoleUrl_Accepts_2048Exactly` |
| 4 | The link is the address as written, byte-equal to the 1.x link | `ConsoleUrlTests.ConsoleLink_MixedCaseHost_DefaultPort443And80_TrailingSlashes_ByteEqualsFrozenFormatter` |
| 5 | The webhook and email bodies are the 1.x bodies byte for byte (an email body ends at its last line, with the host's newline); the Slack and Teams bodies are content-equal (title, text, link URL), the provider framing them itself. Markup, control characters and long values; a title or subject is bounded at 250 characters, where 1.x cut at 300 | `BodyParityTests.BodyParity_SlackTeamsEmailWebhook_FrozenFormatter_BytesOrContentEqual` (one theory for the four channels) |
| 6 | A file endpoint sends signed, and a receiver sees the 1.x body and a signature that verifies | `PlanningAndProvisioningTests.Provisioning_FileEndpoint_SendsSigned_ReceiverSeesSameBodyAndSignature` |
| 7 | The file's allowlist: a private host and its port, a mixed-case host with the default port; another port is refused | `Provisioning_FileAllowlistPrivateHost_DefaultPortAndMixedCaseHost_Allowed_OtherPortRefused` |
| 8 | A file that names a tenant other than the platform's stops the pipeline's start, writes nothing, and degrades readiness | `Provisioning_FileForOtherTenant_PipelineNotStarted_ReadinessDegraded` |
| 9 | The escalation selection (severity, rule, environment; a host alert has none) names the policy label; two matches take the first by key and warn | `EscalationSelectionTests.Escalation_SelectionBySeverityRuleEnv_NamesPolicyLabel`, `Escalation_TwoMatch_FirstByKeyOrdinal_Warns` |
| 10 | A selection whose pipeline policy was deleted still delivers the notification, without escalating, and warns | `Escalation_PipelinePolicyDeletedLater_NotificationStillDelivered_NoEscalation` |
| 11 | The escalation API: `steps` in a body is `422`, a key the pipeline has no policy for is `422 no_pipeline_policy`, operators are forbidden, another tenant is forbidden | `EscalationApi_StepsInBody_422`, `…_NoPipelinePolicy_422`, `…_OperatorForbidden`, `…_OtherTenantClaim_Forbidden` |
| 12 | A transition handed over twice is planned once, and two replicas racing make one request | `PlanNotifications_DeliveredTwice_PlannedOnce`, `PlanNotifications_TwoReplicasRace_OneRequest` |
| 13 | The removed routes and the host command are gone | `RemovedRoutes_Webhooks_Migrate_ShadowReport_404`, `HostCommand_NotificationsMigrate_Exit2` |
| 14 | The migration drops the legacy tables and keeps the selection rows, on real legacy rows and on a fresh database | `Migration_UpgradeFromLegacyRows_DropsTables_SelectionRowsAndHoldsKept`, `Migration_FreshDatabase_Passes` |
| 15 | What shadow kept is settled; a silence's hold and an acknowledgement are untouched | `Migration_ShadowKeptHoldAndParkedSend_DecidedKeptInShadow_SilenceHoldUntouched` |
| 16 | The dead-man has no shadow cause and the others still fire | `DeadManSenderTests.DeadMan_NoShadowCause_OtherCausesStillFire_Async` |
| 17 | An alert's notifications are its correlation key, its silences and its incidents | `LegacyRemovalTests.AlertNotifications_ReturnsCorrelationKey_SilencesIncidents` |

## What was removed, and the tests that went with it

Projects `Juice.Observability.Webhooks` and `Juice.Observability.Notifications.Migration`; the shadow comparer and its report; the routes
`/webhooks/**`, `/notifications/migrate` and `/notifications/shadow-report`; the `notifications migrate` host command (it says it was
removed and exits 2); the `Pipeline` switch; the meters `juice.observability.webhooks.*`, `juice.observability.notifications.shadow.*` and
`…notifications.dry_duplicates`; the status's `webhooks` backlog and worker. Tests that only exercised them are deleted: the webhook
dispatcher, security-of-delivery, operations and API tests, the notification flow (delivery, grouping, escalation steps) tests, the alias, the
migration and its QA tests, the shadow comparer and switch tests, the pipeline-switch tests and the old/new receiver compatibility test.

## Limits

- **The 405 on a removed route.** A removed route answers `404` to a `GET`. Any other method answers `405`, from the host's Console fallback
  (`GET` only), the only route left on the path: the same answer as for any unknown API path.
- **Not tried here:** a real Slack, Teams or TLS mail server; the 1.x acceptance said the same.
