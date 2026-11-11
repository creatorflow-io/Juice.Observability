using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class SloRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "slo_days",
                columns: table => new
                {
                    slo_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    total = table.Column<long>(type: "bigint", nullable: false),
                    good = table.Column<long>(type: "bigint", nullable: false),
                    minutes = table.Column<long>(type: "bigint", nullable: false),
                    eligible = table.Column<long>(type: "bigint", nullable: false),
                    bad = table.Column<long>(type: "bigint", nullable: false),
                    excluded_health = table.Column<long>(type: "bigint", nullable: false),
                    excluded_cancelled = table.Column<long>(type: "bigint", nullable: false),
                    excluded_client_errors = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_slo_days", x => new { x.slo_id, x.day, x.version });
                    table.ForeignKey(
                        name: "fk_slo_days_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_slo_days_slos_slo_id",
                        column: x => x.slo_id,
                        principalTable: "slos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "slo_hours",
                columns: table => new
                {
                    slo_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hour = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    total = table.Column<long>(type: "bigint", nullable: false),
                    good = table.Column<long>(type: "bigint", nullable: false),
                    minutes = table.Column<long>(type: "bigint", nullable: false),
                    eligible = table.Column<long>(type: "bigint", nullable: false),
                    bad = table.Column<long>(type: "bigint", nullable: false),
                    excluded_health = table.Column<long>(type: "bigint", nullable: false),
                    excluded_cancelled = table.Column<long>(type: "bigint", nullable: false),
                    excluded_client_errors = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_slo_hours", x => new { x.slo_id, x.hour, x.version });
                    table.ForeignKey(
                        name: "fk_slo_hours_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_slo_hours_slos_slo_id",
                        column: x => x.slo_id,
                        principalTable: "slos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_slo_days_day",
                table: "slo_days",
                column: "day");

            migrationBuilder.CreateIndex(
                name: "ix_slo_days_tenant_id",
                table: "slo_days",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_slo_hours_hour",
                table: "slo_hours",
                column: "hour");

            migrationBuilder.CreateIndex(
                name: "ix_slo_hours_tenant_id",
                table: "slo_hours",
                column: "tenant_id");

            // The totals of what is already counted, so reads are right from the first request after upgrading: hours from their minutes, days
            // from their hours, as SloDays.RecomputeAsync sums them. Minutes an older replica counts after this (a rolling upgrade) are caught up
            // by the counter when it takes the lease (SloDays.CatchUpAsync).
            migrationBuilder.Sql("""
                INSERT INTO slo_hours (tenant_id, slo_id, hour, version, total, good, minutes, eligible, bad, excluded_health, excluded_cancelled, excluded_client_errors)
                SELECT tenant_id, slo_id, date_trunc('hour', minute, 'UTC'), version, sum(total), sum(good), count(*), sum(eligible), sum(bad),
                       sum(excluded_health), sum(excluded_cancelled), sum(excluded_client_errors)
                FROM slo_buckets
                GROUP BY tenant_id, slo_id, date_trunc('hour', minute, 'UTC'), version;
                INSERT INTO slo_days (tenant_id, slo_id, day, version, total, good, minutes, eligible, bad, excluded_health, excluded_cancelled, excluded_client_errors)
                SELECT tenant_id, slo_id, date_trunc('day', hour, 'UTC'), version, sum(total), sum(good), sum(minutes), sum(eligible), sum(bad),
                       sum(excluded_health), sum(excluded_cancelled), sum(excluded_client_errors)
                FROM slo_hours
                GROUP BY tenant_id, slo_id, date_trunc('day', hour, 'UTC'), version;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "slo_days");

            migrationBuilder.DropTable(
                name: "slo_hours");
        }
    }
}
