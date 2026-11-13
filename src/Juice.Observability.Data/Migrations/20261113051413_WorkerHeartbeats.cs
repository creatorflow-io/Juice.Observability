using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkerHeartbeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "worker_heartbeats",
                columns: table => new
                {
                    worker = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    replica = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    version = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    leased = table.Column<bool>(type: "boolean", nullable: false),
                    held = table.Column<bool>(type: "boolean", nullable: false),
                    held_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    interval_seconds = table.Column<double>(type: "double precision", nullable: false),
                    running_since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replica_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_worker_heartbeats", x => new { x.worker, x.replica });
                });

            migrationBuilder.CreateIndex(
                name: "ix_worker_heartbeats_seen_at",
                table: "worker_heartbeats",
                column: "seen_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "worker_heartbeats");
        }
    }
}
