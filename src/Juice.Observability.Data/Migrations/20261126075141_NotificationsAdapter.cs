using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class NotificationsAdapter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_holds",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    silence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    held_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_holds", x => new { x.tenant_id, x.alert_id });
                    table.ForeignKey(
                        name: "fk_alert_holds_alerts_alert_id",
                        column: x => x.alert_id,
                        principalTable: "alerts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_alert_holds_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_holds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    @event = table.Column<string>(name: "event", type: "character varying(40)", maxLength: 40, nullable: false),
                    silence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_holds", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_holds_alerts_alert_id",
                        column: x => x.alert_id,
                        principalTable: "alerts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notification_holds_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notification_parked_calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    correlation_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    request = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    replayed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_parked_calls", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_parked_calls_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_holds_alert_id",
                table: "alert_holds",
                column: "alert_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_holds_alert_id",
                table: "notification_holds",
                column: "alert_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_holds_tenant_id_transition_id",
                table: "notification_holds",
                columns: new[] { "tenant_id", "transition_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_holds_undecided",
                table: "notification_holds",
                columns: new[] { "tenant_id", "silence_id" },
                filter: "decided_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notification_parked_calls_pending",
                table: "notification_parked_calls",
                column: "id",
                filter: "replayed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notification_parked_calls_tenant_id",
                table: "notification_parked_calls",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_holds");

            migrationBuilder.DropTable(
                name: "notification_holds");

            migrationBuilder.DropTable(
                name: "notification_parked_calls");
        }
    }
}
