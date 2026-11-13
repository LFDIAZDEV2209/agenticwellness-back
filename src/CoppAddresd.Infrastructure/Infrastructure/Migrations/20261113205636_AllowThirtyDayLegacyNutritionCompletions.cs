using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowThirtyDayLegacyNutritionCompletions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Ampliación compatible: la app anterior sigue escribiendo 1..7.
            // NOT VALID + transacciones separadas evitan sostener un lock de DDL
            // durante la validación de tablas grandes. No se reescribe ninguna fila.
            migrationBuilder.Sql("ALTER TABLE app.task_completions ADD CONSTRAINT ck_task_completions_plan_day_30 CHECK (nutrition_plan_day_number BETWEEN 1 AND 30) NOT VALID;", suppressTransaction: true);
            migrationBuilder.Sql("ALTER TABLE app.task_completions VALIDATE CONSTRAINT ck_task_completions_plan_day_30;", suppressTransaction: true);
            migrationBuilder.Sql("ALTER TABLE app.task_completions DROP CONSTRAINT ck_task_completions_plan_day_number_range; ALTER TABLE app.task_completions RENAME CONSTRAINT ck_task_completions_plan_day_30 TO ck_task_completions_plan_day_number_range;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversa en ventana sin escritores. Si ya hay días 8..30 se aborta:
            // exportar y resolver esas referencias antes de volver a la app antigua.
            // Nunca se borran completaciones ni se transforma el día del menú.
            migrationBuilder.Sql("""
                LOCK TABLE app.task_completions IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM app.task_completions WHERE nutrition_plan_day_number > 7) THEN
                        RAISE EXCEPTION 'Reversa requiere resolver referencias nutricionales de días 8..30';
                    END IF;
                END $$;
                ALTER TABLE app.task_completions DROP CONSTRAINT ck_task_completions_plan_day_number_range;
                ALTER TABLE app.task_completions ADD CONSTRAINT ck_task_completions_plan_day_number_range CHECK (nutrition_plan_day_number BETWEEN 1 AND 7);
                """);
        }
    }
}
