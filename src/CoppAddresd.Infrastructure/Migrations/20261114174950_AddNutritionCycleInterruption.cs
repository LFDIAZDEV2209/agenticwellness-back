using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNutritionCycleInterruption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.AddColumn<DateTime>(
                name: "interrupted_at",
                schema: "app",
                table: "nutrition_cycles",
                type: "timestamptz",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversa de mantenimiento: no perder la fecha que limita la continuidad tras un retiro.
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                LOCK TABLE app.nutrition_cycles IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM app.nutrition_cycles WHERE interrupted_at IS NOT NULL) THEN
                    RAISE EXCEPTION 'Conservar fechas de retiro antes de retirar interrupted_at.';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "interrupted_at",
                schema: "app",
                table: "nutrition_cycles");
        }
    }
}
