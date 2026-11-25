using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWellnessJourneyDemoGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wellness_journey_generations",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    draft_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    draft_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    error_code = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wellness_journey_generations", x => x.id);
                    table.CheckConstraint("ck_wellness_journey_attempts", "attempts > 0");
                    table.CheckConstraint("ck_wellness_journey_lease", "(status = 'running' AND lease_id IS NOT NULL AND lease_until IS NOT NULL) OR (status <> 'running' AND lease_id IS NULL AND lease_until IS NULL)");
                    table.CheckConstraint("ck_wellness_journey_result", "(status = 'ready') = (result_json IS NOT NULL)");
                    table.CheckConstraint("ck_wellness_journey_status", "status IN ('pending', 'running', 'ready', 'failed')");
                    table.ForeignKey(
                        name: "FK_wellness_journey_generations_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_wellness_journey_claimable",
                schema: "app",
                table: "wellness_journey_generations",
                columns: new[] { "created_at", "id" },
                filter: "status IN ('pending', 'running')");

            migrationBuilder.CreateIndex(
                name: "ix_wellness_journey_patient_id",
                schema: "app",
                table: "wellness_journey_generations",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "ux_wellness_journey_payment_id",
                schema: "app",
                table: "wellness_journey_generations",
                column: "payment_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wellness_journey_generations",
                schema: "app");
        }
    }
}
