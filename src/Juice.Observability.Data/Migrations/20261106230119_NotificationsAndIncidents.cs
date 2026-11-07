using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationsAndIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_webhook_deliveries_transition_id_endpoint_id",
                table: "webhook_deliveries");

            migrationBuilder.AddColumn<string[]>(
                name: "environments",
                table: "webhook_endpoints",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<int>(
                name: "group_window_seconds",
                table: "webhook_endpoints",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "webhook_endpoints",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Webhook");

            migrationBuilder.AddColumn<string>(
                name: "managed_by",
                table: "webhook_endpoints",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "config");

            migrationBuilder.AddColumn<string[]>(
                name: "recipients",
                table: "webhook_endpoints",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<int>(
                name: "escalation_step",
                table: "webhook_deliveries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "grouped_into",
                table: "webhook_deliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "suppressed_by",
                table: "webhook_deliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "managed_by",
                table: "webhook_allowlist",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "config");

            migrationBuilder.AddColumn<string>(
                name: "managed_by",
                table: "rules",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "config");

            migrationBuilder.CreateTable(
                name: "escalation_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    rule_keys = table.Column<string[]>(type: "text[]", nullable: false),
                    environments = table.Column<string[]>(type: "text[]", nullable: false),
                    minimum_severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    stop_on_acknowledge = table.Column<bool>(type: "boolean", nullable: false),
                    steps = table.Column<string>(type: "jsonb", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_escalation_policies", x => x.id);
                    table.ForeignKey(
                        name: "fk_escalation_policies_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incidents", x => x.id);
                    table.ForeignKey(
                        name: "fk_incidents_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "silences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    comment = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    rule_keys = table.Column<string[]>(type: "text[]", nullable: false),
                    service = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    environment = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    host = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    maximum_severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    all_alerts = table.Column<bool>(type: "boolean", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    repeat_weekly = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_silences", x => x.id);
                    table.ForeignKey(
                        name: "fk_silences_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "alert_escalations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    next_step = table.Column<int>(type: "integer", nullable: false),
                    next_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stop_reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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
                name: "incident_alerts",
                columns: table => new
                {
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_alerts", x => new { x.incident_id, x.alert_id });
                    table.ForeignKey(
                        name: "fk_incident_alerts_alerts_alert_id",
                        column: x => x.alert_id,
                        principalTable: "alerts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_incident_alerts_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_incident_alerts_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incident_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    from_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    to_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_incident_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_incident_events_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_incident_events_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_tenant_id_alert_id",
                table: "webhook_deliveries",
                columns: new[] { "tenant_id", "alert_id" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_transition_id_endpoint_id_escalation_step",
                table: "webhook_deliveries",
                columns: new[] { "transition_id", "endpoint_id", "escalation_step" },
                unique: true);

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
                name: "ix_escalation_policies_tenant_id_key",
                table: "escalation_policies",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_incident_alerts_alert_id",
                table: "incident_alerts",
                column: "alert_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_alerts_tenant_id",
                table: "incident_alerts",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_events_incident_id_at",
                table: "incident_events",
                columns: new[] { "incident_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_incident_events_tenant_id",
                table: "incident_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_incidents_tenant_id_state",
                table: "incidents",
                columns: new[] { "tenant_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_silences_tenant_id_ends_at",
                table: "silences",
                columns: new[] { "tenant_id", "ends_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_escalations");

            migrationBuilder.DropTable(
                name: "incident_alerts");

            migrationBuilder.DropTable(
                name: "incident_events");

            migrationBuilder.DropTable(
                name: "silences");

            migrationBuilder.DropTable(
                name: "escalation_policies");

            migrationBuilder.DropTable(
                name: "incidents");

            migrationBuilder.DropIndex(
                name: "ix_webhook_deliveries_tenant_id_alert_id",
                table: "webhook_deliveries");

            migrationBuilder.DropIndex(
                name: "ix_webhook_deliveries_transition_id_endpoint_id_escalation_step",
                table: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "environments",
                table: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "group_window_seconds",
                table: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "managed_by",
                table: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "recipients",
                table: "webhook_endpoints");

            migrationBuilder.DropColumn(
                name: "escalation_step",
                table: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "grouped_into",
                table: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "suppressed_by",
                table: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "managed_by",
                table: "webhook_allowlist");

            migrationBuilder.DropColumn(
                name: "managed_by",
                table: "rules");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_transition_id_endpoint_id",
                table: "webhook_deliveries",
                columns: new[] { "transition_id", "endpoint_id" },
                unique: true);
        }
    }
}
