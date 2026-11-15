using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPatientAiBriefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "patient_ai_briefs",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    draft_json = table.Column<string>(type: "jsonb", nullable: false),
                    model_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    generated_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "draft")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patient_ai_briefs", x => x.id);
                    table.CheckConstraint("ck_patient_ai_briefs_draft", "jsonb_typeof(draft_json) = 'object'");
                    table.CheckConstraint("ck_patient_ai_briefs_input_hash", "input_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_patient_ai_briefs_kind", "kind IN ('patient_summary', 'weekly_plan_suggestions')");
                    table.CheckConstraint("ck_patient_ai_briefs_model", "model_name IS NULL OR length(btrim(model_name)) > 0");
                    table.CheckConstraint("ck_patient_ai_briefs_state", "state IN ('draft', 'applied', 'dismissed')");
                    table.ForeignKey(
                        name: "FK_patient_ai_briefs_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "Borradores IA del paciente; sin publicación ni aplicación automática de cambios clínicos.");

            migrationBuilder.CreateIndex(
                name: "ix_patient_ai_briefs_patient_kind_generated",
                schema: "app",
                table: "patient_ai_briefs",
                columns: new[] { "patient_id", "kind", "generated_at", "id" },
                descending: new[] { false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "patient_ai_briefs",
                schema: "app");
        }
    }
}
