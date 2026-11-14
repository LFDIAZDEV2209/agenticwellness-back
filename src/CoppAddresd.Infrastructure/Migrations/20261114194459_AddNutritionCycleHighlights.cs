using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNutritionCycleHighlights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.CreateTable(
                name: "nutrition_cycle_highlights",
                schema: "app",
                columns: table => new
                {
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intake_log_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pinned_by = table.Column<Guid>(type: "uuid", nullable: false),
                    pinned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nutrition_cycle_highlights", x => new { x.cycle_id, x.intake_log_id });
                    table.ForeignKey(
                        name: "FK_nutrition_cycle_highlights_nutrition_cycles_cycle_id",
                        column: x => x.cycle_id,
                        principalSchema: "app",
                        principalTable: "nutrition_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_nutrition_cycle_highlights_nutrition_intake_logs_intake_log~",
                        column: x => x.intake_log_id,
                        principalSchema: "app",
                        principalTable: "nutrition_intake_logs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cycle_highlights_intake",
                schema: "app",
                table: "nutrition_cycle_highlights",
                column: "intake_log_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversa de mantenimiento: conservar los ejemplos y sus notas antes de retirar la tabla.
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                LOCK TABLE app.nutrition_cycle_highlights IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM app.nutrition_cycle_highlights) THEN
                    RAISE EXCEPTION 'Conservar ejemplos anclados antes de revertir nutrition_cycle_highlights.';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "nutrition_cycle_highlights",
                schema: "app");
        }
    }
}
