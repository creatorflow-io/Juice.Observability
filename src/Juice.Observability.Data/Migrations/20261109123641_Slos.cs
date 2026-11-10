using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class Slos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "slos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sli = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    service = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    environment = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    current_version = table.Column<int>(type: "integer", nullable: false),
                    retired = table.Column<bool>(type: "boolean", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    counted_through = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_slos", x => x.id);
                    table.ForeignKey(
                        name: "fk_slos_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "slo_buckets",
                columns: table => new
                {
                    slo_id = table.Column<Guid>(type: "uuid", nullable: false),
                    minute = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    total = table.Column<long>(type: "bigint", nullable: false),
                    good = table.Column<long>(type: "bigint", nullable: false),
                    eligible = table.Column<long>(type: "bigint", nullable: false),
                    bad = table.Column<long>(type: "bigint", nullable: false),
                    excluded_health = table.Column<long>(type: "bigint", nullable: false),
                    excluded_cancelled = table.Column<long>(type: "bigint", nullable: false),
                    excluded_client_errors = table.Column<long>(type: "bigint", nullable: false),
                    histogram = table.Column<long[]>(type: "bigint[]", nullable: false),
                    counted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_slo_buckets", x => new { x.slo_id, x.minute });
                    table.ForeignKey(
                        name: "fk_slo_buckets_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_slo_buckets_slos_slo_id",
                        column: x => x.slo_id,
                        principalTable: "slos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "slo_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slo_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    definition = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target = table.Column<double>(type: "double precision", nullable: false),
                    effective_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_slo_versions", x => x.id);
                    table.ForeignKey(
                        name: "fk_slo_versions_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_slo_versions_slos_slo_id",
                        column: x => x.slo_id,
                        principalTable: "slos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_slo_buckets_minute",
                table: "slo_buckets",
                column: "minute");

            migrationBuilder.CreateIndex(
                name: "ix_slo_buckets_tenant_id",
                table: "slo_buckets",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_slo_versions_slo_id_version",
                table: "slo_versions",
                columns: new[] { "slo_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_slo_versions_tenant_id",
                table: "slo_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_slos_tenant_id_key",
                table: "slos",
                columns: new[] { "tenant_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "slo_buckets");

            migrationBuilder.DropTable(
                name: "slo_versions");

            migrationBuilder.DropTable(
                name: "slos");
        }
    }
}
