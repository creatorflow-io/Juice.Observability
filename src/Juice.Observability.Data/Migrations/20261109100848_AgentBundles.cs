using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentBundles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_bundles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_bundles", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_bundles_agent_commands_command_id",
                        column: x => x.command_id,
                        principalTable: "agent_commands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agent_bundles_agents_agent_id",
                        column: x => x.agent_id,
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agent_bundles_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_bundles_agent_id_created_at",
                table: "agent_bundles",
                columns: new[] { "agent_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_bundles_command_id",
                table: "agent_bundles",
                column: "command_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_bundles_expires_at",
                table: "agent_bundles",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_agent_bundles_tenant_id",
                table: "agent_bundles",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_bundles");
        }
    }
}
