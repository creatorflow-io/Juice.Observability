using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class PipelineModeKept : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pipeline",
                table: "notification_parked_calls",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pipeline",
                table: "notification_holds",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason",
                table: "notification_holds",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "resolve_asked",
                table: "notification_holds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Holds kept before: the reason their silence says (none, Guid.Empty, is the pipeline not started); their mode wasn't kept. A
            // silence's resolve was resolved at plan then (the release pass resolved only those kept while the pipeline was down).
            migrationBuilder.Sql(
                "UPDATE notification_holds SET reason = CASE WHEN silence_id = '00000000-0000-0000-0000-000000000000' THEN 'pipeline_unavailable' ELSE 'silence' END, "
                + "resolve_asked = (event = 'alert.resolved' AND silence_id <> '00000000-0000-0000-0000-000000000000')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pipeline",
                table: "notification_parked_calls");

            migrationBuilder.DropColumn(
                name: "pipeline",
                table: "notification_holds");

            migrationBuilder.DropColumn(
                name: "reason",
                table: "notification_holds");

            migrationBuilder.DropColumn(
                name: "resolve_asked",
                table: "notification_holds");
        }
    }
}
