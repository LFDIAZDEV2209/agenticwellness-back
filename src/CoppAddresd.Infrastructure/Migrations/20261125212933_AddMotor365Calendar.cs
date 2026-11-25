using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMotor365Calendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "calendar_anchor_date",
                schema: "app",
                table: "person",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "calendar_anchor_day",
                schema: "app",
                table: "person",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "calendar_timezone_id",
                schema: "app",
                table: "person",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_motor365_person_calendar",
                schema: "app",
                table: "person",
                sql: "(calendar_timezone_id IS NULL AND calendar_anchor_date IS NULL AND calendar_anchor_day IS NULL) OR (calendar_timezone_id IS NOT NULL AND btrim(calendar_timezone_id) <> '' AND calendar_anchor_date IS NOT NULL AND calendar_anchor_day IS NOT NULL AND calendar_anchor_day BETWEEN 1 AND 365)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_motor365_person_calendar",
                schema: "app",
                table: "person");

            migrationBuilder.DropColumn(
                name: "calendar_anchor_date",
                schema: "app",
                table: "person");

            migrationBuilder.DropColumn(
                name: "calendar_anchor_day",
                schema: "app",
                table: "person");

            migrationBuilder.DropColumn(
                name: "calendar_timezone_id",
                schema: "app",
                table: "person");
        }
    }
}
