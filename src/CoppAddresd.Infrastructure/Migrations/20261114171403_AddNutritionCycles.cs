using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNutritionCycles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.CreateTable(
                name: "nutrition_cycles",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: true),
                    start_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    state = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nutrition_cycles", x => x.id);
                    table.CheckConstraint("ck_nutrition_cycle_publication", "(state = 'borrador' AND number IS NULL AND start_date IS NULL AND end_date IS NULL) OR (state <> 'borrador' AND number > 0 AND number IS NOT NULL AND start_date IS NOT NULL AND end_date IS NOT NULL AND end_date >= start_date AND end_date - start_date < 30)");
                    table.CheckConstraint("ck_nutrition_cycle_state", "state IN ('borrador','en_curso','por_revisar','revisado','interrumpido')");
                    table.ForeignKey(
                        name: "FK_nutrition_cycles_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "uq_nutrition_cycles_patient_number",
                schema: "app",
                table: "nutrition_cycles",
                columns: new[] { "patient_id", "number" },
                unique: true);

            // Backfill derivado, sin alterar prescripciones ni consumir números para borradores.
            // No se asume que haya exactamente cuatro ciclos en otros entornos.
            migrationBuilder.Sql("""
                LOCK TABLE app.nutrition_prescriptions IN SHARE ROW EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT cycle_id FROM app.nutrition_prescriptions GROUP BY cycle_id
                    HAVING count(DISTINCT patient_id) > 1) THEN
                    RAISE EXCEPTION 'Un ciclo pertenece a varios pacientes; corregir antes de migrar.';
                  END IF;
                END $$;
                WITH headers AS (
                  SELECT DISTINCT ON (cycle_id) cycle_id, patient_id, start_date, end_date,
                    approved_at, created_at, state = 'aprobado' AND approved_at IS NOT NULL AS published
                  FROM app.nutrition_prescriptions
                  ORDER BY cycle_id, (state = 'aprobado' AND approved_at IS NOT NULL) DESC,
                    version DESC, created_at, id
                ), numbered AS (
                  SELECT *, row_number() OVER (PARTITION BY patient_id
                    ORDER BY start_date, approved_at, created_at, cycle_id)::int AS n
                  FROM headers WHERE published
                )
                INSERT INTO app.nutrition_cycles (id, patient_id, number, start_date, end_date, state)
                  SELECT cycle_id, patient_id, n, start_date, end_date, 'en_curso' FROM numbered
                  UNION ALL
                  SELECT cycle_id, patient_id, NULL, NULL, NULL, 'borrador' FROM headers WHERE NOT published;

                -- Puente de compatibilidad: también cubre escritores anteriores a esta migración.
                -- El mismo lock de paciente que NutritionCaseStore serializa la numeración.
                CREATE FUNCTION app.ensure_nutrition_cycle() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE owner_id uuid;
                BEGIN
                  PERFORM id FROM app.patient_profiles WHERE id = NEW.patient_id FOR UPDATE;
                  INSERT INTO app.nutrition_cycles (id, patient_id, state)
                    VALUES (NEW.cycle_id, NEW.patient_id, 'borrador') ON CONFLICT (id) DO NOTHING;
                  SELECT patient_id INTO owner_id FROM app.nutrition_cycles WHERE id = NEW.cycle_id;
                  IF owner_id IS DISTINCT FROM NEW.patient_id THEN
                    RAISE EXCEPTION 'El ciclo no pertenece al paciente de la prescripción.' USING ERRCODE = '23503';
                  END IF;
                  IF NEW.state = 'aprobado' AND NEW.approved_at IS NOT NULL THEN
                    UPDATE app.nutrition_cycles SET
                      number = (SELECT coalesce(max(number), 0) + 1 FROM app.nutrition_cycles WHERE patient_id = NEW.patient_id),
                      start_date = NEW.start_date, end_date = NEW.end_date, state = 'en_curso'
                    WHERE id = NEW.cycle_id AND number IS NULL;
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER ensure_nutrition_cycle BEFORE INSERT OR UPDATE OF cycle_id, patient_id, state, approved_at
                  ON app.nutrition_prescriptions FOR EACH ROW EXECUTE FUNCTION app.ensure_nutrition_cycle();
                ALTER TABLE app.nutrition_prescriptions ADD CONSTRAINT "FK_nutrition_prescriptions_nutrition_cycles_cycle_id"
                  FOREIGN KEY (cycle_id) REFERENCES app.nutrition_cycles(id) ON DELETE RESTRICT NOT VALID;
                ALTER TABLE app.nutrition_prescriptions VALIDATE CONSTRAINT "FK_nutrition_prescriptions_nutrition_cycles_cycle_id";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversa de mantenimiento: respalda cabeceras/numeración antes de Down.
            // Conserva todas las prescripciones; al volver a Up se reconstruyen las cabeceras.
            // Revisiones/retiros ya no son derivables: se impide perderlos silenciosamente.
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                LOCK TABLE app.nutrition_prescriptions IN SHARE ROW EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM app.nutrition_cycles WHERE state IN ('revisado','interrumpido')) THEN
                    RAISE EXCEPTION 'Exportar y conservar revisiones/retiros antes de revertir nutrition_cycles.';
                  END IF;
                END $$;
                DROP TRIGGER ensure_nutrition_cycle ON app.nutrition_prescriptions;
                DROP FUNCTION app.ensure_nutrition_cycle();
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_nutrition_prescriptions_nutrition_cycles_cycle_id",
                schema: "app",
                table: "nutrition_prescriptions");

            migrationBuilder.DropTable(
                name: "nutrition_cycles",
                schema: "app");
        }
    }
}
