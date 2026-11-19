using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.WhatsApp.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistWhatsAppReplyFragments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "agent_started",
                schema: "whatsapp",
                table: "raw_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string[]>(
                name: "reply_fragments",
                schema: "whatsapp",
                table: "raw_events",
                type: "text[]",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "agent_started",
                schema: "whatsapp",
                table: "raw_events");

            migrationBuilder.DropColumn(
                name: "reply_fragments",
                schema: "whatsapp",
                table: "raw_events");
        }
    }
}
