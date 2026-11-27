using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Auth.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivacyDispositionEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClosedBy",
                schema: "auth",
                table: "PrivacyDeletionCases",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CompletionSourceCount",
                schema: "auth",
                table: "PrivacyDeletionCases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InventoryReference",
                schema: "auth",
                table: "PrivacyDeletionCases",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RestoreReference",
                schema: "auth",
                table: "PrivacyDeletionCases",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PrivacyDispositionEvidence",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RemainingCount = table.Column<long>(type: "bigint", nullable: false),
                    RowsErased = table.Column<int>(type: "integer", nullable: true),
                    EvidenceReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ActionReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PolicyReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ArchiveReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RestoreReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ReviewAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RecordedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivacyDispositionEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrivacyDispositionEvidence_PrivacyDeletionCases_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "auth",
                        principalTable: "PrivacyDeletionCases",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyDispositionEvidence_RequestId_SourceKey_EvidenceRefe~",
                schema: "auth",
                table: "PrivacyDispositionEvidence",
                columns: new[] { "RequestId", "SourceKey", "EvidenceReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyDispositionEvidence_RequestId_SourceKey_RecordedAt",
                schema: "auth",
                table: "PrivacyDispositionEvidence",
                columns: new[] { "RequestId", "SourceKey", "RecordedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrivacyDispositionEvidence",
                schema: "auth");

            migrationBuilder.DropColumn(
                name: "ClosedBy",
                schema: "auth",
                table: "PrivacyDeletionCases");

            migrationBuilder.DropColumn(
                name: "CompletionSourceCount",
                schema: "auth",
                table: "PrivacyDeletionCases");

            migrationBuilder.DropColumn(
                name: "InventoryReference",
                schema: "auth",
                table: "PrivacyDeletionCases");

            migrationBuilder.DropColumn(
                name: "RestoreReference",
                schema: "auth",
                table: "PrivacyDeletionCases");
        }
    }
}
