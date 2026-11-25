using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Auth.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivacyRequestsAndReviewedExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrivacyRequests",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Format = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExportPreparedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivacyRequests", x => x.Id);
                    table.CheckConstraint("ck_privacy_request_kind_format", "(\"Kind\" = 'export' AND \"Format\" IS NOT NULL AND \"Format\" IN ('json', 'csv'))\n    OR (\"Kind\" = 'deletion' AND \"Format\" IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "PrivacyExportArtifacts",
                schema: "auth",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublishedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProtectedContent = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivacyExportArtifacts", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_PrivacyExportArtifacts_PrivacyRequests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "auth",
                        principalTable: "PrivacyRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyExportArtifacts_ExpiresAt",
                schema: "auth",
                table: "PrivacyExportArtifacts",
                column: "ExpiresAt",
                filter: "\"ProtectedContent\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PrivacyRequests_UserId_Kind",
                schema: "auth",
                table: "PrivacyRequests",
                columns: new[] { "UserId", "Kind" },
                unique: true,
                filter: "\"ClosedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrivacyExportArtifacts",
                schema: "auth");

            migrationBuilder.DropTable(
                name: "PrivacyRequests",
                schema: "auth");
        }
    }
}
