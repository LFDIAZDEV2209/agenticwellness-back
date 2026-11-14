using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExerciseRoutineContraindications : Migration
    {
        /// <summary>Columna nullable sin respaldo masivo: compatible con binarios anteriores.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.AddColumn<string>(
                name: "contraindications",
                schema: "app",
                table: "exercise_routines",
                type: "text",
                nullable: true);
        }

        /// <summary>Reversa de mantenimiento; rechaza perder advertencias ya escritas.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                LOCK TABLE app.exercise_routines IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM app.exercise_routines WHERE contraindications IS NOT NULL) THEN
                    RAISE EXCEPTION 'No se puede revertir: existen contraindicaciones; conservarlas antes de retirar la columna.';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "contraindications",
                schema: "app",
                table: "exercise_routines");
        }
    }
}
