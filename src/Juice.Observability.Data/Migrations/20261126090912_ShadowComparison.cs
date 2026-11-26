using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShadowComparison : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shadow_comparisons",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    escalation_step = table.Column<int>(type: "integer", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    @event = table.Column<string>(name: "event", type: "character varying(40)", maxLength: 40, nullable: false),
                    legacy = table.Column<int>(type: "integer", nullable: false),
                    @new = table.Column<int>(name: "new", type: "integer", nullable: false),
                    differences = table.Column<int>(type: "integer", nullable: false),
                    unexplained = table.Column<int>(type: "integer", nullable: false),
                    compared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    escalation_step = table.Column<int>(type: "integer", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    @event = table.Column<string>(name: "event", type: "character varying(40)", maxLength: 40, nullable: false),
                    endpoint_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    rule = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "shadow_comparisons");

            migrationBuilder.DropTable(
                name: "shadow_differences");
        }
    }
}
