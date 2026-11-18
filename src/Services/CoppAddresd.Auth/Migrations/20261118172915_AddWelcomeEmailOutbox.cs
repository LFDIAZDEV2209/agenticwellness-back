using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Auth.Migrations
{
    /// <inheritdoc />
    public partial class AddWelcomeEmailOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WelcomeEmailDeliveries",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeduplicationKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    InvitationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadyAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WelcomeEmailDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WelcomeEmailDeliveries_Invitations_InvitationId",
                        column: x => x.InvitationId,
                        principalSchema: "auth",
                        principalTable: "Invitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WelcomeEmailDeliveries_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "auth",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WelcomeEmailDeliveries_DeduplicationKey",
                schema: "auth",
                table: "WelcomeEmailDeliveries",
                column: "DeduplicationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WelcomeEmailDeliveries_InvitationId",
                schema: "auth",
                table: "WelcomeEmailDeliveries",
                column: "InvitationId");

            migrationBuilder.CreateIndex(
                name: "IX_WelcomeEmailDeliveries_Status_NextAttemptAt",
                schema: "auth",
                table: "WelcomeEmailDeliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WelcomeEmailDeliveries_UserId",
                schema: "auth",
                table: "WelcomeEmailDeliveries",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WelcomeEmailDeliveries",
                schema: "auth");
        }
    }
}
