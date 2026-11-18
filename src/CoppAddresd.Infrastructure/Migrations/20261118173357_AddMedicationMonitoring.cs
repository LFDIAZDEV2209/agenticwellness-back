using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMedicationMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "medication_audit_entries",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    origin = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    resource = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    actor_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    request_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purpose = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_audit_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_medication_audit_entries_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medication_effect_reports",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    treatment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    symptom = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reported_by = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_effect_reports", x => x.id);
                    table.CheckConstraint("ck_med_effect_severity", "severity IN ('mild','moderate','severe')");
                    table.CheckConstraint("ck_med_effect_status", "status IN ('open','follow_up','resolved')");
                    table.ForeignKey(
                        name: "FK_medication_effect_reports_medication_treatments_treatment_id",
                        column: x => x.treatment_id,
                        principalSchema: "app",
                        principalTable: "medication_treatments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_effect_reports_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medication_monitoring_alerts",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    treatment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dedup_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    condition_active = table.Column<bool>(type: "boolean", nullable: false),
                    assigned_to = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_monitoring_alerts", x => x.id);
                    table.CheckConstraint("ck_med_alert_status", "status IN ('open','in_progress','resolved')");
                    table.ForeignKey(
                        name: "FK_medication_monitoring_alerts_medication_treatments_treatmen~",
                        column: x => x.treatment_id,
                        principalSchema: "app",
                        principalTable: "medication_treatments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_monitoring_alerts_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medication_monitoring_operations",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    result_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_monitoring_operations", x => x.id);
                    table.ForeignKey(
                        name: "FK_medication_monitoring_operations_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medication_supplies",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    treatment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    balance = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_supplies", x => x.id);
                    table.CheckConstraint("ck_med_supply_revision", "revision > 0");
                    table.ForeignKey(
                        name: "FK_medication_supplies_medication_treatments_treatment_id",
                        column: x => x.treatment_id,
                        principalSchema: "app",
                        principalTable: "medication_treatments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_supplies_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medication_effect_followups",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_effect_followups", x => x.id);
                    table.ForeignKey(
                        name: "FK_medication_effect_followups_medication_effect_reports_repor~",
                        column: x => x.report_id,
                        principalSchema: "app",
                        principalTable: "medication_effect_reports",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_effect_followups_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medication_alert_followups",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_alert_followups", x => x.id);
                    table.ForeignKey(
                        name: "FK_medication_alert_followups_medication_monitoring_alerts_ale~",
                        column: x => x.alert_id,
                        principalSchema: "app",
                        principalTable: "medication_monitoring_alerts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_alert_followups_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "medication_stock_movements",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    supply_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dose_id = table.Column<Guid>(type: "uuid", nullable: true),
                    intake_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    quantity_delta = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    supply_revision = table.Column<int>(type: "integer", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    recorded_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_medication_stock_movements", x => x.id);
                    table.CheckConstraint("ck_med_stock_kind", "kind IN ('initial','refill','adjustment','consumption','correction')");
                    table.ForeignKey(
                        name: "FK_medication_stock_movements_medication_doses_dose_id",
                        column: x => x.dose_id,
                        principalSchema: "app",
                        principalTable: "medication_doses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_stock_movements_medication_intake_events_intake_~",
                        column: x => x.intake_event_id,
                        principalSchema: "app",
                        principalTable: "medication_intake_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_stock_movements_medication_supplies_supply_id",
                        column: x => x.supply_id,
                        principalSchema: "app",
                        principalTable: "medication_supplies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_medication_stock_movements_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_medication_alert_followups_alert_id_created_at",
                schema: "app",
                table: "medication_alert_followups",
                columns: new[] { "alert_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_medication_alert_followups_patient_id",
                schema: "app",
                table: "medication_alert_followups",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "IX_medication_audit_entries_action_created_at",
                schema: "app",
                table: "medication_audit_entries",
                columns: new[] { "action", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_medication_audit_entries_actor_id_created_at",
                schema: "app",
                table: "medication_audit_entries",
                columns: new[] { "actor_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_medication_audit_entries_patient_id_created_at",
                schema: "app",
                table: "medication_audit_entries",
                columns: new[] { "patient_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_medication_effect_followups_patient_id",
                schema: "app",
                table: "medication_effect_followups",
                column: "patient_id");

            migrationBuilder.CreateIndex(
                name: "IX_medication_effect_followups_report_id_created_at",
                schema: "app",
                table: "medication_effect_followups",
                columns: new[] { "report_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_medication_effect_reports_patient_id_created_at",
                schema: "app",
                table: "medication_effect_reports",
                columns: new[] { "patient_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_medication_effect_reports_treatment_id",
                schema: "app",
                table: "medication_effect_reports",
                column: "treatment_id");

            migrationBuilder.CreateIndex(
                name: "IX_medication_monitoring_alerts_patient_id_dedup_key",
                schema: "app",
                table: "medication_monitoring_alerts",
                columns: new[] { "patient_id", "dedup_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_medication_monitoring_alerts_status_updated_at",
                schema: "app",
                table: "medication_monitoring_alerts",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_medication_monitoring_alerts_treatment_id",
                schema: "app",
                table: "medication_monitoring_alerts",
                column: "treatment_id");

            migrationBuilder.CreateIndex(
                name: "IX_medication_monitoring_operations_patient_id_client_request_~",
                schema: "app",
                table: "medication_monitoring_operations",
                columns: new[] { "patient_id", "client_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_medication_stock_movements_dose_id",
                schema: "app",
                table: "medication_stock_movements",
                column: "dose_id");

            migrationBuilder.CreateIndex(
                name: "IX_medication_stock_movements_intake_event_id",
                schema: "app",
                table: "medication_stock_movements",
                column: "intake_event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_medication_stock_movements_patient_id_recorded_at",
                schema: "app",
                table: "medication_stock_movements",
                columns: new[] { "patient_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_medication_stock_movements_supply_id_recorded_at",
                schema: "app",
                table: "medication_stock_movements",
                columns: new[] { "supply_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_medication_supplies_patient_id_expires_on",
                schema: "app",
                table: "medication_supplies",
                columns: new[] { "patient_id", "expires_on" });

            migrationBuilder.CreateIndex(
                name: "IX_medication_supplies_treatment_id",
                schema: "app",
                table: "medication_supplies",
                column: "treatment_id",
                unique: true);
            migrationBuilder.Sql("""
                SELECT audit.attach_table_audit('app', 'medication_supplies', 'id', VARIADIC ARRAY['balance','expires_on']::text[]);
                SELECT audit.attach_table_audit('app', 'medication_effect_reports', 'id', VARIADIC ARRAY['symptom','notes','severity','occurred_at']::text[]);
                SELECT audit.attach_table_audit('app', 'medication_monitoring_alerts', 'id', VARIADIC ARRAY['resolution_note']::text[]);
                CREATE FUNCTION app.medication_monitoring_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Medication history is append-only'; END;
                $$;
                CREATE TRIGGER med_audit_append_only BEFORE UPDATE OR DELETE ON app.medication_audit_entries
                    FOR EACH ROW EXECUTE FUNCTION app.medication_monitoring_append_only();
                CREATE TRIGGER med_stock_append_only BEFORE UPDATE OR DELETE ON app.medication_stock_movements
                    FOR EACH ROW EXECUTE FUNCTION app.medication_monitoring_append_only();
                CREATE TRIGGER med_effect_followup_append_only BEFORE UPDATE OR DELETE ON app.medication_effect_followups
                    FOR EACH ROW EXECUTE FUNCTION app.medication_monitoring_append_only();
                CREATE TRIGGER med_alert_followup_append_only BEFORE UPDATE OR DELETE ON app.medication_alert_followups
                    FOR EACH ROW EXECUTE FUNCTION app.medication_monitoring_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER med_audit_append_only ON app.medication_audit_entries;
                DROP TRIGGER med_stock_append_only ON app.medication_stock_movements;
                DROP TRIGGER med_effect_followup_append_only ON app.medication_effect_followups;
                DROP TRIGGER med_alert_followup_append_only ON app.medication_alert_followups;
                DROP FUNCTION app.medication_monitoring_append_only();
                """);
            migrationBuilder.DropTable(
                name: "medication_alert_followups",
                schema: "app");

            migrationBuilder.DropTable(
                name: "medication_audit_entries",
                schema: "app");

            migrationBuilder.DropTable(
                name: "medication_effect_followups",
                schema: "app");

            migrationBuilder.DropTable(
                name: "medication_monitoring_operations",
                schema: "app");

            migrationBuilder.DropTable(
                name: "medication_stock_movements",
                schema: "app");

            migrationBuilder.DropTable(
                name: "medication_monitoring_alerts",
                schema: "app");

            migrationBuilder.DropTable(
                name: "medication_effect_reports",
                schema: "app");

            migrationBuilder.DropTable(
                name: "medication_supplies",
                schema: "app");
        }
    }
}
