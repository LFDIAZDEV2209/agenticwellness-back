using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMotor365Persistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "engine_params",
                schema: "app",
                columns: table => new
                {
                    version = table.Column<int>(type: "integer", nullable: false),
                    engine_ver = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    parameters_json = table.Column<string>(type: "jsonb", nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engine_params", x => x.version);
                    table.UniqueConstraint("AK_engine_params_version_engine_ver", x => new { x.version, x.engine_ver });
                    table.CheckConstraint("ck_motor365_engine_params_parameters", "jsonb_typeof(parameters_json) = 'object'");
                    table.CheckConstraint("ck_motor365_engine_params_reason", "btrim(reason) <> ''");
                    table.CheckConstraint("ck_motor365_engine_params_version", "version > 0 AND btrim(engine_ver) <> ''");
                });

            migrationBuilder.CreateTable(
                name: "person",
                schema: "app",
                columns: table => new
                {
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sex = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    birth_year = table.Column<int>(type: "integer", nullable: false),
                    height_cm = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    initial_weight_kg = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: false),
                    initial_waist_cm = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    target_weight_kg = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    activity_level = table.Column<int>(type: "integer", nullable: false),
                    goal = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    pace = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    vegetarian = table.Column<bool>(type: "boolean", nullable: false),
                    lactose_free = table.Column<bool>(type: "boolean", nullable: false),
                    fitness_level = table.Column<int>(type: "integer", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    engine_ver = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    plan_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_person", x => x.patient_id);
                    table.CheckConstraint("ck_motor365_person_activity", "activity_level BETWEEN 1 AND 4");
                    table.CheckConstraint("ck_motor365_person_birth_year", "birth_year BETWEEN 1900 AND EXTRACT(YEAR FROM start_date)");
                    table.CheckConstraint("ck_motor365_person_fitness", "fitness_level BETWEEN 0 AND 3");
                    table.CheckConstraint("ck_motor365_person_goal", "goal IN ('LoseWeight', 'GainMuscle', 'Tone')");
                    table.CheckConstraint("ck_motor365_person_height", "height_cm > 0 AND height_cm <= 300");
                    table.CheckConstraint("ck_motor365_person_initial_waist", "initial_waist_cm > 0 AND initial_waist_cm <= 500");
                    table.CheckConstraint("ck_motor365_person_initial_weight", "initial_weight_kg > 0 AND initial_weight_kg <= 1000");
                    table.CheckConstraint("ck_motor365_person_pace", "pace IN ('Slow', 'Moderate', 'Maximum')");
                    table.CheckConstraint("ck_motor365_person_sex", "sex IN ('Female', 'Male')");
                    table.CheckConstraint("ck_motor365_person_target_weight", "target_weight_kg > 0 AND target_weight_kg <= 1000");
                    table.CheckConstraint("ck_motor365_person_versions", "plan_version > 0 AND btrim(engine_ver) <> ''");
                    table.ForeignKey(
                        name: "FK_person_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true, defaultValueSql: "NULLIF(current_setting('audit.user_id', true), '')::uuid"),
                    action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.id);
                    table.CheckConstraint("ck_motor365_audit_log_action", "btrim(action) <> ''");
                    table.ForeignKey(
                        name: "FK_audit_log_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "app",
                        principalTable: "person",
                        principalColumn: "patient_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "consent",
                schema: "app",
                columns: table => new
                {
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    acceptance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_consent", x => new { x.person_id, x.acceptance_id });
                    table.ForeignKey(
                        name: "FK_consent_legal_document_acceptances_acceptance_id",
                        column: x => x.acceptance_id,
                        principalSchema: "app",
                        principalTable: "legal_document_acceptances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_consent_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "app",
                        principalTable: "person",
                        principalColumn: "patient_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "daily_log",
                schema: "app",
                columns: table => new
                {
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<int>(type: "integer", nullable: false),
                    log_date = table.Column<DateOnly>(type: "date", nullable: false),
                    adherence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    sleep_hours = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false),
                    energy = table.Column<int>(type: "integer", nullable: false),
                    pain = table.Column<int>(type: "integer", nullable: false),
                    symptoms = table.Column<string[]>(type: "text[]", nullable: false),
                    weight_kg = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    waist_cm = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    completed_exercise = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_log", x => new { x.person_id, x.day });
                    table.CheckConstraint("ck_motor365_daily_log_adherence", "adherence BETWEEN 0 AND 1");
                    table.CheckConstraint("ck_motor365_daily_log_day", "day BETWEEN 1 AND 365");
                    table.CheckConstraint("ck_motor365_daily_log_energy", "energy BETWEEN 1 AND 5");
                    table.CheckConstraint("ck_motor365_daily_log_pain", "pain BETWEEN 0 AND 10");
                    table.CheckConstraint("ck_motor365_daily_log_sleep", "sleep_hours BETWEEN 0 AND 24");
                    table.CheckConstraint("ck_motor365_daily_log_symptoms", "symptoms <@ ARRAY['mareo','hambre','fatiga','insomnio']::text[]");
                    table.CheckConstraint("ck_motor365_daily_log_waist", "waist_cm > 0 AND waist_cm <= 500");
                    table.CheckConstraint("ck_motor365_daily_log_weight", "weight_kg > 0 AND weight_kg <= 1000");
                    table.ForeignKey(
                        name: "FK_daily_log_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "app",
                        principalTable: "person",
                        principalColumn: "patient_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "engine_event",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<int>(type: "integer", nullable: false),
                    plan_version = table.Column<int>(type: "integer", nullable: false),
                    params_version = table.Column<int>(type: "integer", nullable: false),
                    engine_ver = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    calorie_delta = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    rate_percent = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    message_es = table.Column<string>(type: "text", nullable: false),
                    message_en = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engine_event", x => x.id);
                    table.CheckConstraint("ck_motor365_engine_event_day", "day BETWEEN 1 AND 365");
                    table.CheckConstraint("ck_motor365_engine_event_kind", "kind IN ('meta', 'modo', 'sin', 'ajuste')");
                    table.CheckConstraint("ck_motor365_engine_event_version", "plan_version > 0");
                    table.ForeignKey(
                        name: "FK_engine_event_engine_params_params_version_engine_ver",
                        columns: x => new { x.params_version, x.engine_ver },
                        principalSchema: "app",
                        principalTable: "engine_params",
                        principalColumns: new[] { "version", "engine_ver" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_engine_event_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "app",
                        principalTable: "person",
                        principalColumn: "patient_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "plan_snapshot",
                schema: "app",
                columns: table => new
                {
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_version = table.Column<int>(type: "integer", nullable: false),
                    params_version = table.Column<int>(type: "integer", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engine_ver = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    plan_json = table.Column<string>(type: "jsonb", nullable: false),
                    generated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    screening_complete = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_snapshot", x => new { x.person_id, x.plan_version, x.params_version });
                    table.CheckConstraint("ck_motor365_plan_snapshot_plan", "jsonb_typeof(plan_json) = 'object'");
                    table.CheckConstraint("ck_motor365_plan_snapshot_screening", "screening_complete");
                    table.CheckConstraint("ck_motor365_plan_snapshot_version", "plan_version > 0");
                    table.ForeignKey(
                        name: "FK_plan_snapshot_engine_params_params_version_engine_ver",
                        columns: x => new { x.params_version, x.engine_ver },
                        principalSchema: "app",
                        principalTable: "engine_params",
                        principalColumns: new[] { "version", "engine_ver" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_plan_snapshot_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "app",
                        principalTable: "person",
                        principalColumn: "patient_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "screening",
                schema: "app",
                columns: table => new
                {
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    par_q_1 = table.Column<short>(type: "smallint", nullable: true),
                    par_q_2 = table.Column<short>(type: "smallint", nullable: true),
                    par_q_3 = table.Column<short>(type: "smallint", nullable: true),
                    par_q_4 = table.Column<short>(type: "smallint", nullable: true),
                    par_q_5 = table.Column<short>(type: "smallint", nullable: true),
                    par_q_6 = table.Column<short>(type: "smallint", nullable: true),
                    par_q_7 = table.Column<short>(type: "smallint", nullable: true),
                    nqs_1 = table.Column<short>(type: "smallint", nullable: true),
                    nqs_2 = table.Column<short>(type: "smallint", nullable: true),
                    nqs_3 = table.Column<short>(type: "smallint", nullable: true),
                    nqs_4 = table.Column<short>(type: "smallint", nullable: true),
                    nqs_5 = table.Column<short>(type: "smallint", nullable: true),
                    nqs_6 = table.Column<short>(type: "smallint", nullable: true),
                    answers_complete = table.Column<bool>(type: "boolean", nullable: false, computedColumnSql: "par_q_1 IS NOT NULL AND par_q_2 IS NOT NULL AND par_q_3 IS NOT NULL AND par_q_4 IS NOT NULL AND par_q_5 IS NOT NULL AND par_q_6 IS NOT NULL AND par_q_7 IS NOT NULL AND nqs_1 IS NOT NULL AND nqs_2 IS NOT NULL AND nqs_3 IS NOT NULL AND nqs_4 IS NOT NULL AND nqs_5 IS NOT NULL AND nqs_6 IS NOT NULL", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_screening", x => new { x.person_id, x.submission_id });
                    table.CheckConstraint("ck_motor365_screening_nqs_1", "nqs_1 IN (0, 1)");
                    table.CheckConstraint("ck_motor365_screening_nqs_2", "nqs_2 IN (0, 1)");
                    table.CheckConstraint("ck_motor365_screening_nqs_3", "nqs_3 IN (0, 1)");
                    table.CheckConstraint("ck_motor365_screening_nqs_4", "nqs_4 IN (0, 1)");
                    table.CheckConstraint("ck_motor365_screening_nqs_5", "nqs_5 IN (0, 1)");
                    table.CheckConstraint("ck_motor365_screening_nqs_6", "nqs_6 IN (0, 1)");
                    table.CheckConstraint("ck_motor365_screening_par_q_1", "par_q_1 IN (0, 1)");
                    table.CheckConstraint("ck_motor365_screening_par_q_2", "par_q_2 IN (0, 1)");
                    table.CheckConstraint("ck_motor365_screening_par_q_3", "par_q_3 IN (0, 1)");
                    table.CheckConstraint("ck_motor365_screening_par_q_4", "par_q_4 IN (0, 1)");
                    table.CheckConstraint("ck_motor365_screening_par_q_5", "par_q_5 IN (0, 1)");
                    table.CheckConstraint("ck_motor365_screening_par_q_6", "par_q_6 IN (0, 1)");
                    table.CheckConstraint("ck_motor365_screening_par_q_7", "par_q_7 IN (0, 1)");
                    table.ForeignKey(
                        name: "FK_screening_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "app",
                        principalTable: "person",
                        principalColumn: "patient_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "professional_review",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    professional_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_professional_review", x => x.id);
                    table.CheckConstraint("ck_motor365_professional_review_decision", "decision IN ('Approved', 'Rejected', 'NeedsChanges')");
                    table.ForeignKey(
                        name: "FK_professional_review_professionals_professional_id",
                        column: x => x.professional_id,
                        principalSchema: "erp",
                        principalTable: "professionals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_professional_review_screening_person_id_submission_id",
                        columns: x => new { x.person_id, x.submission_id },
                        principalSchema: "app",
                        principalTable: "screening",
                        principalColumns: new[] { "person_id", "submission_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_occurred_at",
                schema: "app",
                table: "audit_log",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_person_id_occurred_at",
                schema: "app",
                table: "audit_log",
                columns: new[] { "person_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_consent_acceptance_id",
                schema: "app",
                table: "consent",
                column: "acceptance_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_consent_person_id_recorded_at",
                schema: "app",
                table: "consent",
                columns: new[] { "person_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "IX_daily_log_log_date",
                schema: "app",
                table: "daily_log",
                column: "log_date");

            migrationBuilder.CreateIndex(
                name: "IX_daily_log_person_id_log_date",
                schema: "app",
                table: "daily_log",
                columns: new[] { "person_id", "log_date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_engine_event_occurred_at",
                schema: "app",
                table: "engine_event",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_engine_event_params_version_engine_ver",
                schema: "app",
                table: "engine_event",
                columns: new[] { "params_version", "engine_ver" });

            migrationBuilder.CreateIndex(
                name: "IX_engine_event_person_id_day",
                schema: "app",
                table: "engine_event",
                columns: new[] { "person_id", "day" });

            migrationBuilder.CreateIndex(
                name: "IX_engine_params_created_at",
                schema: "app",
                table: "engine_params",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_plan_snapshot_generated_at",
                schema: "app",
                table: "plan_snapshot",
                column: "generated_at");

            migrationBuilder.CreateIndex(
                name: "IX_plan_snapshot_params_version_engine_ver",
                schema: "app",
                table: "plan_snapshot",
                columns: new[] { "params_version", "engine_ver" });

            migrationBuilder.CreateIndex(
                name: "IX_plan_snapshot_person_id_submission_id",
                schema: "app",
                table: "plan_snapshot",
                columns: new[] { "person_id", "submission_id" });

            migrationBuilder.CreateIndex(
                name: "IX_professional_review_person_id_reviewed_at",
                schema: "app",
                table: "professional_review",
                columns: new[] { "person_id", "reviewed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_professional_review_person_id_submission_id",
                schema: "app",
                table: "professional_review",
                columns: new[] { "person_id", "submission_id" });

            migrationBuilder.CreateIndex(
                name: "IX_professional_review_professional_id",
                schema: "app",
                table: "professional_review",
                column: "professional_id");

            migrationBuilder.CreateIndex(
                name: "IX_screening_person_id_recorded_at",
                schema: "app",
                table: "screening",
                columns: new[] { "person_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ux_motor365_screening_completion",
                schema: "app",
                table: "screening",
                columns: new[] { "person_id", "submission_id", "answers_complete" },
                unique: true);

            // La columna generada puede cambiar al completar el borrador, por eso no es una clave de EF.
            migrationBuilder.AddForeignKey(
                name: "fk_motor365_snapshot_complete_screening",
                schema: "app",
                table: "plan_snapshot",
                columns: new[] { "person_id", "submission_id", "screening_complete" },
                principalSchema: "app",
                principalTable: "screening",
                principalColumns: new[] { "person_id", "submission_id", "answers_complete" },
                onUpdate: ReferentialAction.Restrict,
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE FUNCTION app.motor365_reject_audit_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $motor365$
                BEGIN
                    RAISE EXCEPTION 'La bitácora del Motor 365 solo admite inserciones'
                        USING ERRCODE = '55000';
                END;
                $motor365$;

                CREATE TRIGGER motor365_audit_log_append_only
                BEFORE UPDATE OR DELETE OR TRUNCATE ON app.audit_log
                FOR EACH STATEMENT EXECUTE FUNCTION app.motor365_reject_audit_mutation();

                REVOKE UPDATE, DELETE, TRUNCATE ON TABLE app.audit_log FROM PUBLIC;
                DO $motor365_acl$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_user') THEN
                        REVOKE UPDATE, DELETE, TRUNCATE ON TABLE app.audit_log FROM app_user;
                        GRANT SELECT, INSERT ON TABLE app.audit_log TO app_user;
                    END IF;
                END;
                $motor365_acl$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "app");

            // El DROP de la tabla retira su trigger y sus privilegios.
            migrationBuilder.Sql("DROP FUNCTION app.motor365_reject_audit_mutation();");

            migrationBuilder.DropTable(
                name: "consent",
                schema: "app");

            migrationBuilder.DropTable(
                name: "daily_log",
                schema: "app");

            migrationBuilder.DropTable(
                name: "engine_event",
                schema: "app");

            migrationBuilder.DropTable(
                name: "plan_snapshot",
                schema: "app");

            migrationBuilder.DropTable(
                name: "professional_review",
                schema: "app");

            migrationBuilder.DropTable(
                name: "engine_params",
                schema: "app");

            migrationBuilder.DropTable(
                name: "screening",
                schema: "app");

            migrationBuilder.DropTable(
                name: "person",
                schema: "app");
        }
    }
}
