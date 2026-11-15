using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExpandPatientAiBriefModelName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.AlterColumn<string>(
                name: "model_name",
                schema: "app",
                table: "patient_ai_briefs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No truncar nombres reales del proveedor al volver al límite anterior.
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                LOCK TABLE app.patient_ai_briefs IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM app.patient_ai_briefs WHERE length(model_name) > 120) THEN
                    RAISE EXCEPTION 'Conservar modelos de más de 120 caracteres antes de revertir.';
                  END IF;
                END $$;
                """);
            migrationBuilder.AlterColumn<string>(
                name: "model_name",
                schema: "app",
                table: "patient_ai_briefs",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
