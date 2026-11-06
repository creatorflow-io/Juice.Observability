using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChangeEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "current_version",
                table: "service_environments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "current_version_at",
                table: "service_environments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "change_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    environment = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    from_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    to_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    attributes = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    external_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_change_events_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_change_events_resources_resource_id",
                        column: x => x.resource_id,
                        principalTable: "resources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_change_events_services_service_id",
                        column: x => x.service_id,
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_events_resource_id",
                table: "change_events",
                column: "resource_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_events_service_id",
                table: "change_events",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_events_tenant_id_occurred_at",
                table: "change_events",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_change_events_tenant_id_service_id_occurred_at",
                table: "change_events",
                columns: new[] { "tenant_id", "service_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_change_events_tenant_id_source_external_key",
                table: "change_events",
                columns: new[] { "tenant_id", "source", "external_key" },
                unique: true,
                filter: "external_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_events");

            migrationBuilder.DropColumn(
                name: "current_version",
                table: "service_environments");

            migrationBuilder.DropColumn(
                name: "current_version_at",
                table: "service_environments");
        }
    }
}
