using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class SloVersionWithdrawn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "withdrawn_at",
                table: "slo_versions",
                type: "timestamp with time zone",
                nullable: true);

            // Before this column, a version counted as withdrawn when a later version took effect no later than it. Keep those withdrawn.
            migrationBuilder.Sql("""
                UPDATE slo_versions v SET withdrawn_at = v.created_at
                WHERE EXISTS (SELECT 1 FROM slo_versions w WHERE w.slo_id = v.slo_id AND w.version > v.version AND w.effective_from <= v.effective_from);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "withdrawn_at",
                table: "slo_versions");
        }
    }
}
