using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Auth.Migrations
{
    /// <inheritdoc />
    public partial class AddPaidSignupSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AmountCents",
                schema: "auth",
                table: "PaidActivations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE auth."PaidActivations"
                SET "AmountCents" = CASE WHEN "Plan" = 'annual' THEN 11976 ELSE 1996 END
                """);

            migrationBuilder.AddColumn<DateTime>(
                name: "SetupSessionExpiresAt",
                schema: "auth",
                table: "PaidActivations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SetupSessionId",
                schema: "auth",
                table: "PaidActivations",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmountCents",
                schema: "auth",
                table: "PaidActivations");

            migrationBuilder.DropColumn(
                name: "SetupSessionExpiresAt",
                schema: "auth",
                table: "PaidActivations");

            migrationBuilder.DropColumn(
                name: "SetupSessionId",
                schema: "auth",
                table: "PaidActivations");
        }
    }
}
