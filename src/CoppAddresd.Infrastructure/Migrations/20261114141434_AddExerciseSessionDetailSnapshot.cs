using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExerciseSessionDetailSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.AddColumn<JsonElement>(
                name: "detail_snapshot",
                schema: "app",
                table: "task_completions",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                LOCK TABLE app.task_completions IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM app.task_completions WHERE detail_snapshot IS NOT NULL) THEN
                    RAISE EXCEPTION 'No se puede revertir: existen sesiones capturadas; conservarlas antes de retirar la columna.';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "detail_snapshot",
                schema: "app",
                table: "task_completions");
        }
    }
}
