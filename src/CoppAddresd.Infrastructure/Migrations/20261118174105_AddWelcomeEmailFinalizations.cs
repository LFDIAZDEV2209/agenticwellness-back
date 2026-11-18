using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWelcomeEmailFinalizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "welcome_email_finalizations",
                schema: "erp",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invitation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_welcome_email_finalizations", x => x.user_id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_welcome_email_finalizations_next_attempt_at",
                schema: "erp",
                table: "welcome_email_finalizations",
                column: "next_attempt_at",
                filter: "completed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "welcome_email_finalizations",
                schema: "erp");
        }
    }
}
