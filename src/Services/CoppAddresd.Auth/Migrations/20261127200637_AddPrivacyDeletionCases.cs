using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Auth.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivacyDeletionCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrivacyDeletionCases",
                schema: "auth",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    VerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VerifiedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentityEvidence = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PolicyReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DeadlineChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeadlineChangedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    PreferencesErasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PreferencesErasedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    PreferencesErasureEvidence = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PreferencesRowsErased = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivacyDeletionCases", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_PrivacyDeletionCases_PrivacyRequests_RequestId",
                        column: x => x.RequestId,
                        principalSchema: "auth",
                        principalTable: "PrivacyRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrivacyDeletionCases",
                schema: "auth");
        }
    }
}
