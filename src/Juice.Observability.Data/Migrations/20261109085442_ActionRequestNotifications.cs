using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class ActionRequestNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "transition_id",
                table: "webhook_deliveries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "alert_id",
                table: "webhook_deliveries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "action_request_id",
                table: "webhook_deliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_action_request_id_endpoint_id_event",
                table: "webhook_deliveries",
                columns: new[] { "action_request_id", "endpoint_id", "event" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_webhook_deliveries_action_requests_action_request_id",
                table: "webhook_deliveries",
                column: "action_request_id",
                principalTable: "action_requests",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_webhook_deliveries_action_requests_action_request_id",
                table: "webhook_deliveries");

            migrationBuilder.DropIndex(
                name: "ix_webhook_deliveries_action_request_id_endpoint_id_event",
                table: "webhook_deliveries");

            migrationBuilder.DropColumn(
                name: "action_request_id",
                table: "webhook_deliveries");

            migrationBuilder.AlterColumn<Guid>(
                name: "transition_id",
                table: "webhook_deliveries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "alert_id",
                table: "webhook_deliveries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
