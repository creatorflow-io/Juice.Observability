using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class RulesAndAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    operation = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: false),
                    response_body = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => new { x.tenant_id, x.key, x.operation });
                    table.ForeignKey(
                        name: "fk_idempotency_records_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    template = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    definition = table.Column<string>(type: "jsonb", nullable: false),
                    definition_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    loaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rules", x => x.id);
                    table.ForeignKey(
                        name: "fk_rules_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    subject_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    subject_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    breach_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    fired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    recovering_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_value = table.Column<double>(type: "double precision", nullable: true),
                    last_samples = table.Column<long>(type: "bigint", nullable: false),
                    no_data = table.Column<bool>(type: "boolean", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    acknowledge_comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alerts", x => x.id);
                    table.ForeignKey(
                        name: "fk_alerts_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_alerts_rules_rule_id",
                        column: x => x.rule_id,
                        principalTable: "rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "alert_transitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    to_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: true),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_transitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_alert_transitions_alerts_alert_id",
                        column: x => x.alert_id,
                        principalTable: "alerts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_alert_transitions_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_transitions_alert_id_at",
                table: "alert_transitions",
                columns: new[] { "alert_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_alert_transitions_tenant_id",
                table: "alert_transitions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_alerts_rule_id",
                table: "alerts",
                column: "rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_alerts_tenant_id_dedup_key_resolved_at",
                table: "alerts",
                columns: new[] { "tenant_id", "dedup_key", "resolved_at" });

            migrationBuilder.CreateIndex(
                name: "ix_alerts_tenant_id_state",
                table: "alerts",
                columns: new[] { "tenant_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ux_alerts_open_dedup_key",
                table: "alerts",
                columns: new[] { "tenant_id", "dedup_key" },
                unique: true,
                filter: "state <> 'Resolved'");

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_created_at",
                table: "idempotency_records",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_rules_tenant_id_key",
                table: "rules",
                columns: new[] { "tenant_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_transitions");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "alerts");

            migrationBuilder.DropTable(
                name: "rules");
        }
    }
}
