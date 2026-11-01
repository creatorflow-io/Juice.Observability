using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Observability.Data.Migrations
{
    /// <inheritdoc />
    public partial class HealthModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "health_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason_codes = table.Column<string[]>(type: "text[]", nullable: false),
                    policy_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evidence_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evidence_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_evidence_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    since = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "fk_health_snapshots_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_health_snapshots_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "health_transitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    to_state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason_codes = table.Column<string[]>(type: "text[]", nullable: false),
                    policy_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evidence_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    evidence_to = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_health_transitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_health_transitions_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_health_transitions_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_environments",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    environment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_environments", x => new { x.tenant_id, x.service_id, x.environment_id });
                    table.ForeignKey(
                        name: "fk_service_environments_environments_environment_id",
                        column: x => x.environment_id,
                        principalTable: "environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_environments_platform_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "platform_tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_environments_services_service_id",
                        column: x => x.service_id,
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_health_snapshots_environment_id",
                table: "health_snapshots",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_health_snapshots_tenant_id_state",
                table: "health_snapshots",
                columns: new[] { "tenant_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_health_snapshots_tenant_id_subject_type_subject_id_environm",
                table: "health_snapshots",
                columns: new[] { "tenant_id", "subject_type", "subject_id", "environment_id" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_health_transitions_environment_id",
                table: "health_transitions",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_health_transitions_tenant_id_subject_id_evaluated_at",
                table: "health_transitions",
                columns: new[] { "tenant_id", "subject_id", "evaluated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_health_transitions_tenant_id_subject_type_subject_id_enviro",
                table: "health_transitions",
                columns: new[] { "tenant_id", "subject_type", "subject_id", "environment_id", "evidence_to", "policy_version" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_service_environments_environment_id",
                table: "service_environments",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "ix_service_environments_service_id",
                table: "service_environments",
                column: "service_id");

            // Existing installs: every environment a service already had instances in.
            migrationBuilder.Sql("""
                INSERT INTO service_environments (tenant_id, service_id, environment_id, first_seen, last_seen)
                SELECT tenant_id, service_id, environment_id, min(first_seen), max(last_seen)
                FROM service_instances
                GROUP BY tenant_id, service_id, environment_id
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "health_snapshots");

            migrationBuilder.DropTable(
                name: "health_transitions");

            migrationBuilder.DropTable(
                name: "service_environments");
        }
    }
}
