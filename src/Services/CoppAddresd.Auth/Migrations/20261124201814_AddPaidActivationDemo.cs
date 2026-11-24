using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Auth.Migrations
{
    /// <inheritdoc />
    public partial class AddPaidActivationDemo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaidActivations",
                schema: "auth",
                columns: table => new
                {
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    Plan = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    Language = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastEmailRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextEmailAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EmailLeaseUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EmailRevision = table.Column<int>(type: "integer", nullable: false),
                    EmailAttempts = table.Column<int>(type: "integer", nullable: false),
                    RedeemedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LinkedUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaidActivations", x => x.PaymentId);
                    table.ForeignKey(
                        name: "FK_PaidActivations_Users_LinkedUserId",
                        column: x => x.LinkedUserId,
                        principalSchema: "auth",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaidActivations_LinkedUserId",
                schema: "auth",
                table: "PaidActivations",
                column: "LinkedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PaidActivations_NextEmailAttemptAt",
                schema: "auth",
                table: "PaidActivations",
                column: "NextEmailAttemptAt",
                filter: "\"NextEmailAttemptAt\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaidActivations",
                schema: "auth");
        }
    }
}
