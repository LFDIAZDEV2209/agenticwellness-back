using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNutritionCycleReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SET LOCAL lock_timeout = '5s';");
            migrationBuilder.CreateTable(
                name: "nutrition_cycle_reviews",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cycle_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    reviewer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    professional_name = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    summary_json = table.Column<string>(type: "jsonb", nullable: false),
                    ai_draft_json = table.Column<string>(type: "jsonb", nullable: true),
                    ai_draft_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    professional_notes = table.Column<string>(type: "text", nullable: true),
                    patient_message = table.Column<string>(type: "jsonb", nullable: false),
                    critical_alerts_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_prescription_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nutrition_cycle_reviews", x => x.id);
                    table.ForeignKey(
                        name: "FK_nutrition_cycle_reviews_nutrition_cycles_cycle_id",
                        column: x => x.cycle_id,
                        principalSchema: "app",
                        principalTable: "nutrition_cycles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_nutrition_cycle_reviews_nutrition_prescriptions_next_prescr~",
                        column: x => x.next_prescription_id,
                        principalSchema: "app",
                        principalTable: "nutrition_prescriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "nutrition_cycle_attachments",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    length = table.Column<long>(type: "bigint", nullable: false),
                    caption = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    visible_to_patient = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nutrition_cycle_attachments", x => x.id);
                    table.CheckConstraint("ck_nutrition_cycle_attachment_kind", "kind IN ('image','document')");
                    table.ForeignKey(
                        name: "FK_nutrition_cycle_attachments_nutrition_cycle_reviews_review_~",
                        column: x => x.review_id,
                        principalSchema: "app",
                        principalTable: "nutrition_cycle_reviews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_nutrition_cycle_attachments_review",
                schema: "app",
                table: "nutrition_cycle_attachments",
                column: "review_id");

            migrationBuilder.CreateIndex(
                name: "uq_nutrition_cycle_attachment_key",
                schema: "app",
                table: "nutrition_cycle_attachments",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_nutrition_cycle_reviews_next_prescription_id",
                schema: "app",
                table: "nutrition_cycle_reviews",
                column: "next_prescription_id");

            migrationBuilder.CreateIndex(
                name: "uq_nutrition_cycle_reviews_cycle",
                schema: "app",
                table: "nutrition_cycle_reviews",
                column: "cycle_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversa de mantenimiento: las notas, mensajes y adjuntos deben conservarse antes de retirarlos.
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '5s';
                LOCK TABLE app.nutrition_cycle_reviews, app.nutrition_cycle_attachments IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM app.nutrition_cycle_reviews) OR EXISTS (SELECT 1 FROM app.nutrition_cycle_attachments) THEN
                    RAISE EXCEPTION 'Conservar revisiones y adjuntos antes de revertir nutrition_cycle_reviews.';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "nutrition_cycle_attachments",
                schema: "app");

            migrationBuilder.DropTable(
                name: "nutrition_cycle_reviews",
                schema: "app");
        }
    }
}
