using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompleteMedicationPrescriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_med_event_status",
                schema: "app",
                table: "medication_intake_events");

            migrationBuilder.AddColumn<string>(
                name: "reason",
                schema: "app",
                table: "medication_treatment_status_events",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "effective_from",
                schema: "app",
                table: "medication_regimens",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "effective_until",
                schema: "app",
                table: "medication_regimens",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "follow_up_minutes",
                schema: "app",
                table: "medication_regimens",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "in_app_reminder",
                schema: "app",
                table: "medication_regimens",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "interval_hours",
                schema: "app",
                table: "medication_regimens",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "lead_minutes",
                schema: "app",
                table: "medication_regimens",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "push_reminder",
                schema: "app",
                table: "medication_regimens",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "weekday_mask",
                schema: "app",
                table: "medication_regimens",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "whats_app_reminder",
                schema: "app",
                table: "medication_regimens",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "channel",
                schema: "app",
                table: "medication_intake_events",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "medication_patient_state",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<Guid>(type: "uuid", nullable: false),
                    push_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    in_app_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    whats_app_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    lead_minutes = table.Column<int>(type: "integer", nullable: false),
                    follow_up_minutes = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_patient_state", x => x.id);
                    table.ForeignKey(
                        name: "FK_medication_patient_state_patient_profiles_id",
                        column: x => x.id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_med_regimen_recurrence",
                schema: "app",
                table: "medication_regimens",
                sql: "(weekday_mask IS NULL OR weekday_mask BETWEEN 1 AND 127) AND (interval_hours IS NULL OR interval_hours BETWEEN 3 AND 168) AND (weekday_mask IS NULL OR interval_hours IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_med_event_status",
                schema: "app",
                table: "medication_intake_events",
                sql: "status IN ('taken','omitted','pending')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "medication_patient_state",
                schema: "app");

            migrationBuilder.DropCheckConstraint(
                name: "ck_med_regimen_recurrence",
                schema: "app",
                table: "medication_regimens");

            migrationBuilder.DropCheckConstraint(
                name: "ck_med_event_status",
                schema: "app",
                table: "medication_intake_events");

            migrationBuilder.DropColumn(
                name: "reason",
                schema: "app",
                table: "medication_treatment_status_events");

            migrationBuilder.DropColumn(
                name: "effective_from",
                schema: "app",
                table: "medication_regimens");

            migrationBuilder.DropColumn(
                name: "effective_until",
                schema: "app",
                table: "medication_regimens");

            migrationBuilder.DropColumn(
                name: "follow_up_minutes",
                schema: "app",
                table: "medication_regimens");

            migrationBuilder.DropColumn(
                name: "in_app_reminder",
                schema: "app",
                table: "medication_regimens");

            migrationBuilder.DropColumn(
                name: "interval_hours",
                schema: "app",
                table: "medication_regimens");

            migrationBuilder.DropColumn(
                name: "lead_minutes",
                schema: "app",
                table: "medication_regimens");

            migrationBuilder.DropColumn(
                name: "push_reminder",
                schema: "app",
                table: "medication_regimens");

            migrationBuilder.DropColumn(
                name: "weekday_mask",
                schema: "app",
                table: "medication_regimens");

            migrationBuilder.DropColumn(
                name: "whats_app_reminder",
                schema: "app",
                table: "medication_regimens");

            migrationBuilder.DropColumn(
                name: "channel",
                schema: "app",
                table: "medication_intake_events");

            // Las correcciones ya guardadas se conservan durante un rollback.
            migrationBuilder.Sql("""
                ALTER TABLE app.medication_intake_events
                ADD CONSTRAINT ck_med_event_status CHECK (status IN ('taken','omitted')) NOT VALID
                """);
        }
    }
}
