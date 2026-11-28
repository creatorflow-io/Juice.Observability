using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Api.Integrations.Migrations
{
    /// <inheritdoc />
    public partial class IntegrationsInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "integrations");

            migrationBuilder.CreateTable(
                name: "integration_instances",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plugin_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, computedColumnSql: "lower(name)", stored: true),
                    config = table.Column<string>(type: "jsonb", nullable: false),
                    config_version = table.Column<int>(type: "integer", nullable: false),
                    secret_refs = table.Column<string>(type: "jsonb", nullable: false),
                    scope = table.Column<string>(type: "jsonb", nullable: false),
                    interval_seconds = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    state_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    validated_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_validation = table.Column<string>(type: "jsonb", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    run_token = table.Column<Guid>(type: "uuid", nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    last_outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    last_failure = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    last_failure_detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_instances", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "integration_checkpoints",
                schema: "integrations",
                columns: table => new
                {
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stream = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    watermark = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cursor = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_checkpoints", x => new { x.instance_id, x.stream });
                    table.ForeignKey(
                        name: "fk_integration_checkpoints_integration_instances_instance_id",
                        column: x => x.instance_id,
                        principalSchema: "integrations",
                        principalTable: "integration_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_integration_instances_state_next_run_at",
                schema: "integrations",
                table: "integration_instances",
                columns: new[] { "state", "next_run_at" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_instances_tenant_id_name_key",
                schema: "integrations",
                table: "integration_instances",
                columns: new[] { "tenant_id", "name_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integration_checkpoints",
                schema: "integrations");

            migrationBuilder.DropTable(
                name: "integration_instances",
                schema: "integrations");
        }
    }
}
