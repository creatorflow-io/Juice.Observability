using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // What a database kept in shadow holds, so a stale one upgrades safely: in shadow, legacy had already sent what the pipeline kept
            // because it didn't start, so nothing is left to send. A hold a silence covers, and an acknowledgement, are real in any mode: untouched.
            migrationBuilder.Sql("""
                UPDATE notification_holds
                SET outcome = 'dropped', last_error = 'kept_in_shadow', decided_at = now()
                WHERE decided_at IS NULL AND pipeline = 'shadow' AND reason = 'pipeline_unavailable';
                """);
            migrationBuilder.Sql("""
                UPDATE notification_parked_calls
                SET replayed_at = now(), last_error = 'kept_in_shadow'
                WHERE replayed_at IS NULL AND kind = 'send' AND pipeline = 'shadow';
                """);

            migrationBuilder.DropTable(
                name: "alert_escalations");

            migrationBuilder.DropTable(
                name: "shadow_comparisons");

            migrationBuilder.DropTable(
                name: "shadow_differences");

            migrationBuilder.DropTable(
                name: "webhook_allowlist");

            migrationBuilder.DropTable(
                name: "webhook_attempts");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "steps",
                table: "escalation_policies");

            migrationBuilder.DropColumn(
                name: "stop_on_acknowledge",
                table: "escalation_policies");
        }

        /// <inheritdoc />
        // Down restores the schema, not the data: the legacy tables come back empty, and every escalation policy gets empty steps and 1.x's
        // default for stop_on_acknowledge (true), not what its row held. Restore from a backup if that data matters.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "steps",
                table: "escalation_policies",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "stop_on_acknowledge",
                table: "escalation_policies",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "alert_escalations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    next_step = table.Column<int>(type: "integer", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    stop_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_escalations", x => x.id);
                    table.ForeignKey(
                        name: "fk_alert_escalations_alerts_alert_id",
                        column: x => x.alert_id,
                        principalTable: "alerts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_alert_escalations_escalation_policies_policy_id",
                        column: x => x.policy_id,
                        principalTable: "escalation_policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_alert_escalations_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shadow_comparisons",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    escalation_step = table.Column<int>(type: "integer", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    compared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    differences = table.Column<int>(type: "integer", nullable: false),
                    @event = table.Column<string>(name: "event", type: "character varying(40)", maxLength: 40, nullable: false),
                    legacy = table.Column<int>(type: "integer", nullable: false),
                    @new = table.Column<int>(name: "new", type: "integer", nullable: false),
                    unexplained = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shadow_comparisons", x => new { x.tenant_id, x.transition_id, x.escalation_step });
                    table.ForeignKey(
                        name: "fk_shadow_comparisons_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shadow_differences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    endpoint_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    escalation_step = table.Column<int>(type: "integer", nullable: false),
                    @event = table.Column<string>(name: "event", type: "character varying(40)", maxLength: 40, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    rule = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transition_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shadow_differences", x => x.id);
                    table.ForeignKey(
                        name: "fk_shadow_differences_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_allowlist",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    loaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    managed_by = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "config"),
                    ports = table.Column<int[]>(type: "integer[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_allowlist", x => new { x.tenant_id, x.destination });
                    table.ForeignKey(
                        name: "fk_webhook_allowlist_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_endpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    environments = table.Column<string[]>(type: "text[]", nullable: false),
                    events = table.Column<string[]>(type: "text[]", nullable: false),
                    group_window_seconds = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Webhook"),
                    loaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    managed_by = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "config"),
                    minimum_severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recipients = table.Column<string[]>(type: "text[]", nullable: false),
                    rule_keys = table.Column<string[]>(type: "text[]", nullable: false),
                    secret_ref = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_endpoints", x => x.id);
                    table.ForeignKey(
                        name: "fk_webhook_endpoints_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: false),
                    escalation_step = table.Column<int>(type: "integer", nullable: false),
                    @event = table.Column<string>(name: "event", type: "character varying(40)", maxLength: 40, nullable: false),
                    first_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    grouped_into = table.Column<Guid>(type: "uuid", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_status_code = table.Column<int>(type: "integer", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    suppressed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transition_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_action_requests_action_request_id",
                        column: x => x.action_request_id,
                        principalTable: "action_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_alerts_alert_id",
                        column: x => x.alert_id,
                        principalTable: "alerts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_webhook_deliveries_webhook_endpoints_endpoint_id",
                        column: x => x.endpoint_id,
                        principalTable: "webhook_endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    number = table.Column<int>(type: "integer", nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_attempts", x => x.id);
                    table.ForeignKey(
                        name: "fk_webhook_attempts_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_webhook_attempts_webhook_deliveries_delivery_id",
                        column: x => x.delivery_id,
                        principalTable: "webhook_deliveries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_escalations_alert_id_policy_id",
                table: "alert_escalations",
                columns: new[] { "alert_id", "policy_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_alert_escalations_policy_id",
                table: "alert_escalations",
                column: "policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_escalations_state_next_at",
                table: "alert_escalations",
                columns: new[] { "state", "next_at" });

            migrationBuilder.CreateIndex(
                name: "ix_alert_escalations_tenant_id",
                table: "alert_escalations",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_shadow_comparisons_tenant_id_compared_at",
                table: "shadow_comparisons",
                columns: new[] { "tenant_id", "compared_at" });

            migrationBuilder.CreateIndex(
                name: "ix_shadow_differences_tenant_id_detected_at_rule_kind",
                table: "shadow_differences",
                columns: new[] { "tenant_id", "detected_at", "rule", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_shadow_differences_tenant_id_transition_id_escalation_step",
                table: "shadow_differences",
                columns: new[] { "tenant_id", "transition_id", "escalation_step" });

            migrationBuilder.CreateIndex(
                name: "ix_shadow_differences_unexplained",
                table: "shadow_differences",
                columns: new[] { "tenant_id", "detected_at" },
                filter: "rule IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_attempts_delivery_id_number",
                table: "webhook_attempts",
                columns: new[] { "delivery_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_attempts_tenant_id",
                table: "webhook_attempts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_action_request_id_endpoint_id_event",
                table: "webhook_deliveries",
                columns: new[] { "action_request_id", "endpoint_id", "event" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_alert_id",
                table: "webhook_deliveries",
                column: "alert_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_endpoint_id",
                table: "webhook_deliveries",
                column: "endpoint_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_escalation_steps",
                table: "webhook_deliveries",
                column: "created_at",
                filter: "escalation_step > 0");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_status_next_attempt_at",
                table: "webhook_deliveries",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_tenant_id_alert_id",
                table: "webhook_deliveries",
                columns: new[] { "tenant_id", "alert_id" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_tenant_id_endpoint_id_created_at",
                table: "webhook_deliveries",
                columns: new[] { "tenant_id", "endpoint_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_transition_id_endpoint_id_escalation_step",
                table: "webhook_deliveries",
                columns: new[] { "transition_id", "endpoint_id", "escalation_step" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_endpoints_tenant_id_key",
                table: "webhook_endpoints",
                columns: new[] { "tenant_id", "key" },
                unique: true);
        }
    }
}
