using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMotor365PauseDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "engine_pause_day",
                schema: "app",
                columns: table => new
                {
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<int>(type: "integer", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    scheduled = table.Column<bool>(type: "boolean", nullable: false),
                    safety_exercise = table.Column<bool>(type: "boolean", nullable: false),
                    plan_version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_engine_pause_day", x => new { x.person_id, x.day });
                    table.CheckConstraint("ck_motor365_pause_day", "day BETWEEN 1 AND 365");
                    table.CheckConstraint("ck_motor365_pause_plan_version", "plan_version >= 1");
                    table.ForeignKey(
                        name: "FK_engine_pause_day_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "app",
                        principalTable: "person",
                        principalColumn: "patient_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_engine_pause_day_person_id_effective_date",
                schema: "app",
                table: "engine_pause_day",
                columns: new[] { "person_id", "effective_date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "engine_pause_day",
                schema: "app");
        }
    }
}
