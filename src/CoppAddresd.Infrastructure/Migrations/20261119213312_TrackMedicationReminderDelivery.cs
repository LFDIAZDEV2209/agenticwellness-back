using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TrackMedicationReminderDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "scheduled_at",
                schema: "app",
                table: "medication_reminder_deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "snoozed",
                schema: "app",
                table: "medication_reminder_deliveries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "dose_revision",
                schema: "app",
                table: "medication_intake_events",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "scheduled_at",
                schema: "app",
                table: "medication_reminder_deliveries");

            migrationBuilder.DropColumn(
                name: "snoozed",
                schema: "app",
                table: "medication_reminder_deliveries");

            migrationBuilder.DropColumn(
                name: "dose_revision",
                schema: "app",
                table: "medication_intake_events");
        }
    }
}
