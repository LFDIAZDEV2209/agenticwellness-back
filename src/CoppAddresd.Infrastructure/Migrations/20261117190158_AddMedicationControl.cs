using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMedicationControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "medication_treatments",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    medication_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    product_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    origin = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    client_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_treatments", x => x.id);
                    table.CheckConstraint("ck_med_treatment_revision", "revision > 0");
                    table.CheckConstraint("ck_med_treatment_status", "status IN ('active','paused','completed')");
                    table.CheckConstraint("ck_med_treatment_type", "product_type IN ('medication','vitamin','supplement')");
                    table.ForeignKey(
                        name: "FK_medication_treatments_medications_medication_id",
                        column: x => x.medication_id,
                        principalSchema: "app",
                        principalTable: "medications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_treatments_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "patient_module_access",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    module_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patient_module_access", x => x.id);
                    table.ForeignKey(
                        name: "FK_patient_module_access_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medication_regimens",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    treatment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    dosage = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    unit = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    route = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    instructions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    time_zone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_regimens", x => x.id);
                    table.CheckConstraint("ck_med_regimen_dates", "end_date IS NULL OR end_date >= start_date");
                    table.CheckConstraint("ck_med_regimen_quantity", "dosage > 0");
                    table.ForeignKey(
                        name: "FK_medication_regimens_medication_treatments_treatment_id",
                        column: x => x.treatment_id,
                        principalSchema: "app",
                        principalTable: "medication_treatments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medication_schedule_slots",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    regimen_id = table.Column<Guid>(type: "uuid", nullable: false),
                    local_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_schedule_slots", x => x.id);
                    table.ForeignKey(
                        name: "FK_medication_schedule_slots_medication_regimens_regimen_id",
                        column: x => x.regimen_id,
                        principalSchema: "app",
                        principalTable: "medication_regimens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medication_doses",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    treatment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    regimen_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slot_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scheduled_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    time_zone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_doses", x => x.id);
                    table.CheckConstraint("ck_med_dose_status", "status IN ('pending','taken','omitted','cancelled')");
                    table.ForeignKey(
                        name: "FK_medication_doses_medication_regimens_regimen_id",
                        column: x => x.regimen_id,
                        principalSchema: "app",
                        principalTable: "medication_regimens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_doses_medication_schedule_slots_slot_id",
                        column: x => x.slot_id,
                        principalSchema: "app",
                        principalTable: "medication_schedule_slots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_doses_medication_treatments_treatment_id",
                        column: x => x.treatment_id,
                        principalSchema: "app",
                        principalTable: "medication_treatments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_doses_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medication_intake_events",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    treatment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dose_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    corrects_event_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_intake_events", x => x.id);
                    table.CheckConstraint("ck_med_event_status", "status IN ('taken','omitted')");
                    table.ForeignKey(
                        name: "FK_medication_intake_events_medication_doses_dose_id",
                        column: x => x.dose_id,
                        principalSchema: "app",
                        principalTable: "medication_doses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_intake_events_medication_intake_events_corrects_~",
                        column: x => x.corrects_event_id,
                        principalSchema: "app",
                        principalTable: "medication_intake_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_intake_events_medication_treatments_treatment_id",
                        column: x => x.treatment_id,
                        principalSchema: "app",
                        principalTable: "medication_treatments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_intake_events_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_med_dose_patient_at",
                schema: "app",
                table: "medication_doses",
                columns: new[] { "patient_id", "scheduled_at" });

            migrationBuilder.CreateIndex(
                name: "IX_medication_doses_regimen_id",
                schema: "app",
                table: "medication_doses",
                column: "regimen_id");

            migrationBuilder.CreateIndex(
                name: "IX_medication_doses_treatment_id",
                schema: "app",
                table: "medication_doses",
                column: "treatment_id");

            migrationBuilder.CreateIndex(
                name: "ux_med_dose_slot_at",
                schema: "app",
                table: "medication_doses",
                columns: new[] { "slot_id", "scheduled_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_med_event_patient_at",
                schema: "app",
                table: "medication_intake_events",
                columns: new[] { "patient_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_medication_intake_events_corrects_event_id",
                schema: "app",
                table: "medication_intake_events",
                column: "corrects_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_medication_intake_events_dose_id",
                schema: "app",
                table: "medication_intake_events",
                column: "dose_id");

            migrationBuilder.CreateIndex(
                name: "IX_medication_intake_events_treatment_id",
                schema: "app",
                table: "medication_intake_events",
                column: "treatment_id");

            migrationBuilder.CreateIndex(
                name: "ux_med_event_operation",
                schema: "app",
                table: "medication_intake_events",
                columns: new[] { "patient_id", "client_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_med_regimen_version",
                schema: "app",
                table: "medication_regimens",
                columns: new[] { "treatment_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_med_schedule_time",
                schema: "app",
                table: "medication_schedule_slots",
                columns: new[] { "regimen_id", "local_time" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_med_treatment_patient_status",
                schema: "app",
                table: "medication_treatments",
                columns: new[] { "patient_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_medication_treatments_medication_id",
                schema: "app",
                table: "medication_treatments",
                column: "medication_id");

            migrationBuilder.CreateIndex(
                name: "ux_med_treatment_operation",
                schema: "app",
                table: "medication_treatments",
                columns: new[] { "patient_id", "client_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_patient_module_access",
                schema: "app",
                table: "patient_module_access",
                columns: new[] { "patient_id", "module_code" },
                unique: true);

            // Auditoría existente, excluyendo texto clínico y hashes de operación del payload general.
            migrationBuilder.Sql("""
                SELECT audit.attach_table_audit('app', 'medication_treatments', 'id', VARIADIC ARRAY['name','request_hash']::text[]);
                SELECT audit.attach_table_audit('app', 'medication_regimens', 'id', VARIADIC ARRAY['dosage','unit','route','instructions','time_zone','start_date','end_date']::text[]);
                SELECT audit.attach_table_audit('app', 'medication_schedule_slots', 'id', VARIADIC ARRAY['local_time']::text[]);
                SELECT audit.attach_table_audit('app', 'medication_doses', 'id', VARIADIC ARRAY['scheduled_at','occurred_at','time_zone']::text[]);
                SELECT audit.attach_table_audit('app', 'medication_intake_events', 'id', VARIADIC ARRAY['reason','request_hash','occurred_at']::text[]);
                SELECT audit.attach_table_audit('app', 'patient_module_access', 'id', VARIADIC ARRAY[]::text[]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "medication_intake_events",
                schema: "app");

            migrationBuilder.DropTable(
                name: "patient_module_access",
                schema: "app");

            migrationBuilder.DropTable(
                name: "medication_doses",
                schema: "app");

            migrationBuilder.DropTable(
                name: "medication_schedule_slots",
                schema: "app");

            migrationBuilder.DropTable(
                name: "medication_regimens",
                schema: "app");

            migrationBuilder.DropTable(
                name: "medication_treatments",
                schema: "app");
        }
    }
}
