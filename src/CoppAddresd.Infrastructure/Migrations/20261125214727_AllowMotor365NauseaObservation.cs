using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowMotor365NauseaObservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_motor365_daily_log_symptoms",
                schema: "app",
                table: "daily_log");

            migrationBuilder.AddCheckConstraint(
                name: "ck_motor365_daily_log_symptoms",
                schema: "app",
                table: "daily_log",
                sql: "symptoms <@ ARRAY['mareo','hambre','fatiga','insomnio','nauseas']::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_motor365_daily_log_symptoms",
                schema: "app",
                table: "daily_log");

            // Conserva las observaciones existentes y vuelve a exigir el catálogo anterior
            // en nuevas escrituras. La validación histórica se hará si se vuelve a ampliar.
            migrationBuilder.Sql("""
                ALTER TABLE app.daily_log ADD CONSTRAINT ck_motor365_daily_log_symptoms
                    CHECK (symptoms <@ ARRAY['mareo','hambre','fatiga','insomnio']::text[]) NOT VALID;
                """);
        }
    }
}
