using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMedicationLifecycleAndOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "medication_dose_operations",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dose_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    accepted_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_dose_operations", x => x.id);
                    table.ForeignKey(
                        name: "FK_medication_dose_operations_medication_doses_dose_id",
                        column: x => x.dose_id,
                        principalSchema: "app",
                        principalTable: "medication_doses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_dose_operations_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medication_treatment_status_events",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    treatment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    effective_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_treatment_status_events", x => x.id);
                    table.CheckConstraint("ck_med_status_revision", "revision > 0");
                    table.CheckConstraint("ck_med_status_value", "status IN ('active','paused','completed')");
                    table.ForeignKey(
                        name: "FK_medication_treatment_status_events_medication_treatments_tr~",
                        column: x => x.treatment_id,
                        principalSchema: "app",
                        principalTable: "medication_treatments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_medication_dose_operations_dose_id",
                schema: "app",
                table: "medication_dose_operations",
                column: "dose_id");

            migrationBuilder.CreateIndex(
                name: "ux_med_dose_operation",
                schema: "app",
                table: "medication_dose_operations",
                columns: new[] { "patient_id", "client_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_med_status_revision",
                schema: "app",
                table: "medication_treatment_status_events",
                columns: new[] { "treatment_id", "revision" },
                unique: true);
            migrationBuilder.Sql("""
                SELECT audit.attach_table_audit('app', 'medication_dose_operations', 'id', VARIADIC ARRAY['request_hash']::text[]);
                SELECT audit.attach_table_audit('app', 'medication_treatment_status_events', 'id', VARIADIC ARRAY['effective_at']::text[]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "medication_dose_operations",
                schema: "app");

            migrationBuilder.DropTable(
                name: "medication_treatment_status_events",
                schema: "app");
        }
    }
}
