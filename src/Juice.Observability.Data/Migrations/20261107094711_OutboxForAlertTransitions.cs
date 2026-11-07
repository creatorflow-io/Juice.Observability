using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class OutboxForAlertTransitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "notifications_planned_at",
                table: "alert_transitions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "outbox_events",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    creation_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    headers = table.Column<string>(type: "text", nullable: false, defaultValue: "{}"),
                    transaction_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    tenant_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_deliveries",
                columns: table => new
                {
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    publisher_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    destination = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    routing_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    creation_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    processed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    next_attempt_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_deliveries", x => x.delivery_id);
                    table.ForeignKey(
                        name: "fk_outbox_deliveries_outbox_events_event_id",
                        column: x => x.event_id,
                        principalTable: "outbox_events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_deliveries_event_id",
                table: "outbox_deliveries",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_deliveries_publisher_key",
                table: "outbox_deliveries",
                column: "publisher_key");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_Pending",
                table: "outbox_deliveries",
                column: "creation_time",
                filter: "state = 0")
                .Annotation("Npgsql:IndexInclude", new[] { "event_id", "publisher_key" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_Recovery",
                table: "outbox_deliveries",
                column: "processed_on",
                filter: "state = 1")
                .Annotation("Npgsql:IndexInclude", new[] { "event_id", "publisher_key" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_Retry",
                table: "outbox_deliveries",
                column: "next_attempt_on",
                filter: "state = 3 AND next_attempt_on IS NOT NULL")
                .Annotation("Npgsql:IndexInclude", new[] { "event_id", "publisher_key" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_transaction_id",
                table: "outbox_events",
                column: "transaction_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_deliveries");

            migrationBuilder.DropTable(
                name: "outbox_events");

            migrationBuilder.DropColumn(
                name: "notifications_planned_at",
                table: "alert_transitions");
        }
    }
}
