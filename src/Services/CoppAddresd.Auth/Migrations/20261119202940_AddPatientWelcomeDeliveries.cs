using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Auth.Migrations
{
    /// <inheritdoc />
    public partial class AddPatientWelcomeDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                schema: "auth",
                table: "WelcomeEmailDeliveries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "PatientId",
                schema: "auth",
                table: "WelcomeEmailDeliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WelcomeEmailDeliveries_PatientId",
                schema: "auth",
                table: "WelcomeEmailDeliveries",
                column: "PatientId",
                unique: true,
                filter: "\"PatientId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_welcome_delivery_subject",
                schema: "auth",
                table: "WelcomeEmailDeliveries",
                sql: "(\"UserId\" IS NOT NULL AND \"PatientId\" IS NULL)\n    OR (\"UserId\" IS NULL AND \"PatientId\" IS NOT NULL AND \"InvitationId\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WelcomeEmailDeliveries_PatientId",
                schema: "auth",
                table: "WelcomeEmailDeliveries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_welcome_delivery_subject",
                schema: "auth",
                table: "WelcomeEmailDeliveries");

            migrationBuilder.DropColumn(
                name: "PatientId",
                schema: "auth",
                table: "WelcomeEmailDeliveries");

            migrationBuilder.AlterColumn<Guid>(
                name: "UserId",
                schema: "auth",
                table: "WelcomeEmailDeliveries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
