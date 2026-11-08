using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentEnrollmentFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_enrollment_failures",
                columns: table => new
                {
                    address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    window_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failures = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_enrollment_failures", x => x.address);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_enrollment_failures_window_start",
                table: "agent_enrollment_failures",
                column: "window_start");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_enrollment_failures");
        }
    }
}
