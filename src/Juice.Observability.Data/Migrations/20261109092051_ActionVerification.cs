using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class ActionVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "alert_id",
                table: "action_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "target_environment_id",
                table: "action_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "target_id",
                table: "action_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_type",
                table: "action_requests",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification",
                table: "action_requests",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");   // existing requests had no target

            migrationBuilder.AddColumn<string>(
                name: "verification_note",
                table: "action_requests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "verified_at",
                table: "action_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_action_requests_verification",
                table: "action_requests",
                column: "verification");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_action_requests_verification",
                table: "action_requests");

            migrationBuilder.DropColumn(
                name: "alert_id",
                table: "action_requests");

            migrationBuilder.DropColumn(
                name: "target_environment_id",
                table: "action_requests");

            migrationBuilder.DropColumn(
                name: "target_id",
                table: "action_requests");

            migrationBuilder.DropColumn(
                name: "target_type",
                table: "action_requests");

            migrationBuilder.DropColumn(
                name: "verification",
                table: "action_requests");

            migrationBuilder.DropColumn(
                name: "verification_note",
                table: "action_requests");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "action_requests");
        }
    }
}
