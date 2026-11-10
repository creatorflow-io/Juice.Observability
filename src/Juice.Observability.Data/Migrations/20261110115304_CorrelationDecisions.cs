using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class CorrelationDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "correlation_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    second_alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    evidence = table.Column<string>(type: "jsonb", nullable: false),
                    weights_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_correlation_decisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_correlation_decisions_alerts_first_alert_id",
                        column: x => x.first_alert_id,
                        principalTable: "alerts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_correlation_decisions_alerts_second_alert_id",
                        column: x => x.second_alert_id,
                        principalTable: "alerts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_correlation_decisions_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_correlation_decisions_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_correlation_decisions_first_alert_id",
                table: "correlation_decisions",
                column: "first_alert_id");

            migrationBuilder.CreateIndex(
                name: "ix_correlation_decisions_incident_id",
                table: "correlation_decisions",
                column: "incident_id");

            migrationBuilder.CreateIndex(
                name: "ix_correlation_decisions_second_alert_id",
                table: "correlation_decisions",
                column: "second_alert_id");

            migrationBuilder.CreateIndex(
                name: "ix_correlation_decisions_tenant_id_first_alert_id_second_alert",
                table: "correlation_decisions",
                columns: new[] { "tenant_id", "first_alert_id", "second_alert_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "correlation_decisions");
        }
    }
}
