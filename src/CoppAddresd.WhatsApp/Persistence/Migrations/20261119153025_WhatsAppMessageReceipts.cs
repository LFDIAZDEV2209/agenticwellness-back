using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.WhatsApp.Persistence.Migrations
{
    /// <summary>
    /// KAN-54: recibos de entrega/lectura de Meta para todos los salientes.
    /// <list type="bullet">
    /// <item><c>whatsapp.message_receipts</c>: append-only, único (wamid, status) para idempotencia.</item>
    /// <item><c>whatsapp.outbound_messages</c>: ledger de fragmentos del chat; wamid único y nulo en
    /// envíos rechazados (PostgreSQL permite varios NULL en un índice único).</item>
    /// <item><c>reminder_deliveries.receipt_*</c>: último estado notificado de cada plantilla.</item>
    /// </list>
    /// Solo agrega objetos nuevos: sin reescritura de tablas ni bloqueo prolongado.
    /// Rollback: <c>dotnet ef database update PersistWhatsAppReplyFragments --project
    /// src/CoppAddresd.WhatsApp --context WhatsAppDbContext</c>. El Down elimina las dos tablas y
    /// las columnas receipt_*; se pierde el histórico de recibos (no afecta envíos ni SOS).
    /// </summary>
    public partial class WhatsAppMessageReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "receipt_at",
                schema: "whatsapp",
                table: "reminder_deliveries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipt_error_code",
                schema: "whatsapp",
                table: "reminder_deliveries",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipt_status",
                schema: "whatsapp",
                table: "reminder_deliveries",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "message_receipts",
                schema: "whatsapp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wamid = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    recipient_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_message_receipts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbound_messages",
                schema: "whatsapp",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wamid = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    wa_id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    raw_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fragment_index = table.Column<int>(type: "integer", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "sent"),
                    status_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbound_messages", x => x.id);
                    table.ForeignKey(
                        name: "FK_outbound_messages_raw_events_raw_event_id",
                        column: x => x.raw_event_id,
                        principalSchema: "whatsapp",
                        principalTable: "raw_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reminder_deliveries_wamid",
                schema: "whatsapp",
                table: "reminder_deliveries",
                column: "wamid",
                filter: "wamid IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_message_receipts_occurred_at",
                schema: "whatsapp",
                table: "message_receipts",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_message_receipts_wamid_status",
                schema: "whatsapp",
                table: "message_receipts",
                columns: new[] { "wamid", "status" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbound_messages_raw_event_id",
                schema: "whatsapp",
                table: "outbound_messages",
                column: "raw_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_outbound_messages_wamid",
                schema: "whatsapp",
                table: "outbound_messages",
                column: "wamid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "message_receipts",
                schema: "whatsapp");

            migrationBuilder.DropTable(
                name: "outbound_messages",
                schema: "whatsapp");

            migrationBuilder.DropIndex(
                name: "IX_reminder_deliveries_wamid",
                schema: "whatsapp",
                table: "reminder_deliveries");

            migrationBuilder.DropColumn(
                name: "receipt_at",
                schema: "whatsapp",
                table: "reminder_deliveries");

            migrationBuilder.DropColumn(
                name: "receipt_error_code",
                schema: "whatsapp",
                table: "reminder_deliveries");

            migrationBuilder.DropColumn(
                name: "receipt_status",
                schema: "whatsapp",
                table: "reminder_deliveries");
        }
    }
}
