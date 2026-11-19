using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUsRxTermsCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "name",
                schema: "app",
                table: "medication_treatments",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<string>(
                name: "rx_cui",
                schema: "app",
                table: "medication_treatments",
                type: "character varying(18)",
                maxLength: 18,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "rxterms_products",
                schema: "app",
                columns: table => new
                {
                    rx_cui = table.Column<string>(type: "character varying(18)", maxLength: 18, nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    search_text = table.Column<string>(type: "text", nullable: false),
                    release = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rxterms_products", x => x.rx_cui);
                });

            migrationBuilder.CreateIndex(
                name: "IX_medication_treatments_rx_cui",
                schema: "app",
                table: "medication_treatments",
                column: "rx_cui");

            migrationBuilder.CreateIndex(
                name: "ix_rxterms_search_trgm",
                schema: "app",
                table: "rxterms_products",
                column: "search_text",
                filter: "is_active")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.AddForeignKey(
                name: "FK_medication_treatments_rxterms_products_rx_cui",
                schema: "app",
                table: "medication_treatments",
                column: "rx_cui",
                principalSchema: "app",
                principalTable: "rxterms_products",
                principalColumn: "rx_cui",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_medication_treatments_rxterms_products_rx_cui",
                schema: "app",
                table: "medication_treatments");

            migrationBuilder.DropTable(
                name: "rxterms_products",
                schema: "app");

            migrationBuilder.DropIndex(
                name: "IX_medication_treatments_rx_cui",
                schema: "app",
                table: "medication_treatments");

            migrationBuilder.DropColumn(
                name: "rx_cui",
                schema: "app",
                table: "medication_treatments");

            migrationBuilder.AlterColumn<string>(
                name: "name",
                schema: "app",
                table: "medication_treatments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");
        }
    }
}
