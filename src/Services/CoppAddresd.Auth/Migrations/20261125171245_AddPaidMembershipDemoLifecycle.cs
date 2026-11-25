using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Auth.Migrations
{
    /// <inheritdoc />
    public partial class AddPaidMembershipDemoLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "CheckoutKeyHash",
                schema: "auth",
                table: "PaidActivations",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MembershipExpiresAt",
                schema: "auth",
                table: "PaidActivations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MembershipPaid",
                schema: "auth",
                table: "PaidActivations",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "MembershipRevision",
                schema: "auth",
                table: "PaidActivations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "MembershipState",
                schema: "auth",
                table: "PaidActivations",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.CreateTable(
                name: "PaidMembershipDemoEvents",
                schema: "auth",
                columns: table => new
                {
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    AmountCents = table.Column<int>(type: "integer", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaidMembershipDemoEvents", x => new { x.PaymentId, x.EventId });
                    table.ForeignKey(
                        name: "FK_PaidMembershipDemoEvents_PaidActivations_PaymentId",
                        column: x => x.PaymentId,
                        principalSchema: "auth",
                        principalTable: "PaidActivations",
                        principalColumn: "PaymentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaidMembershipDemoEvents_PaymentId_Revision",
                schema: "auth",
                table: "PaidMembershipDemoEvents",
                columns: new[] { "PaymentId", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaidMembershipDemoEvents",
                schema: "auth");

            migrationBuilder.DropColumn(
                name: "CheckoutKeyHash",
                schema: "auth",
                table: "PaidActivations");

            migrationBuilder.DropColumn(
                name: "MembershipExpiresAt",
                schema: "auth",
                table: "PaidActivations");

            migrationBuilder.DropColumn(
                name: "MembershipPaid",
                schema: "auth",
                table: "PaidActivations");

            migrationBuilder.DropColumn(
                name: "MembershipRevision",
                schema: "auth",
                table: "PaidActivations");

            migrationBuilder.DropColumn(
                name: "MembershipState",
                schema: "auth",
                table: "PaidActivations");
        }
    }
}
