using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNutritionIntakeDayReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                schema: "app",
                table: "nutrition_intake_logs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "nutrition_intake_day_reviews",
                schema: "app",
                columns: table => new
                {
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    local_date = table.Column<DateOnly>(type: "date", nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nutrition_intake_day_reviews", x => new { x.patient_id, x.local_date });
                    table.ForeignKey(
                        name: "FK_nutrition_intake_day_reviews_nutrition_cycles_cycle_id",
                        column: x => x.cycle_id,
                        principalSchema: "app",
                        principalTable: "nutrition_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_nutrition_intake_day_reviews_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_intake_day_reviews_cycle",
                schema: "app",
                table: "nutrition_intake_day_reviews",
                column: "cycle_id");
            // Sin UPDATE masivo: los registros anteriores conservan NULL y usan created_at.
            // clock_timestamp comparte reloj con las marcas y no congela la hora al abrir la transacción.
            migrationBuilder.Sql("""
                CREATE FUNCTION app.stamp_nutrition_intake_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF TG_OP = 'INSERT' THEN
                    NEW.updated_at := clock_timestamp();
                  ELSE
                    NEW.created_at := OLD.created_at;
                    IF (to_jsonb(NEW) - 'updated_at' - 'created_at') IS DISTINCT FROM
                       (to_jsonb(OLD) - 'updated_at' - 'created_at') THEN
                      NEW.updated_at := clock_timestamp();
                    ELSE
                      NEW.updated_at := OLD.updated_at;
                    END IF;
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER stamp_nutrition_intake_change BEFORE INSERT OR UPDATE
                  ON app.nutrition_intake_logs FOR EACH ROW EXECUTE FUNCTION app.stamp_nutrition_intake_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversa de mantenimiento: exportar/conservar las revisiones antes de eliminarlas.
            // Sin marcas se puede retirar updated_at: no quedan revisiones que dependan de él.
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                LOCK TABLE app.nutrition_intake_day_reviews IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM app.nutrition_intake_day_reviews) THEN
                    RAISE EXCEPTION 'Conservar revisiones diarias antes de revertir nutrition_intake_day_reviews.';
                  END IF;
                END $$;
                DROP TRIGGER stamp_nutrition_intake_change ON app.nutrition_intake_logs;
                DROP FUNCTION app.stamp_nutrition_intake_change();
                """);
            migrationBuilder.DropTable(
                name: "nutrition_intake_day_reviews",
                schema: "app");

            migrationBuilder.DropColumn(
                name: "updated_at",
                schema: "app",
                table: "nutrition_intake_logs");
        }
    }
}
