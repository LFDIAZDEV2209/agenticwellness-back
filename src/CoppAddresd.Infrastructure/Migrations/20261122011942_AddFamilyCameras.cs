using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFamilyCameras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "family_teams",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    leader_patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_family_teams", x => x.id);
                    table.ForeignKey(
                        name: "FK_family_teams_patient_profiles_leader_patient_id",
                        column: x => x.leader_patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "family_team_invitations",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_family_team_invitations", x => x.id);
                    table.ForeignKey(
                        name: "FK_family_team_invitations_family_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "app",
                        principalTable: "family_teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "family_team_memberships",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_family_team_memberships", x => x.id);
                    table.ForeignKey(
                        name: "FK_family_team_memberships_family_teams_team_id",
                        column: x => x.team_id,
                        principalSchema: "app",
                        principalTable: "family_teams",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_family_team_memberships_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "family_cameras",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    provider = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    shared_membership_id = table.Column<Guid>(type: "uuid", nullable: true),
                    shared_with_administrators = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_family_cameras", x => x.id);
                    table.ForeignKey(
                        name: "FK_family_cameras_family_team_memberships_shared_membership_id",
                        column: x => x.shared_membership_id,
                        principalSchema: "app",
                        principalTable: "family_team_memberships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_family_cameras_patient_profiles_owner_patient_id",
                        column: x => x.owner_patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_family_cameras_owner_patient_id",
                schema: "app",
                table: "family_cameras",
                column: "owner_patient_id");

            migrationBuilder.CreateIndex(
                name: "IX_family_cameras_shared_membership_id",
                schema: "app",
                table: "family_cameras",
                column: "shared_membership_id");

            migrationBuilder.CreateIndex(
                name: "IX_family_team_invitations_code_hash",
                schema: "app",
                table: "family_team_invitations",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_family_team_invitations_team_id",
                schema: "app",
                table: "family_team_invitations",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "IX_family_team_memberships_patient_id",
                schema: "app",
                table: "family_team_memberships",
                column: "patient_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_family_team_memberships_team_id",
                schema: "app",
                table: "family_team_memberships",
                column: "team_id");

            migrationBuilder.CreateIndex(
                name: "IX_family_teams_leader_patient_id",
                schema: "app",
                table: "family_teams",
                column: "leader_patient_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "family_cameras",
                schema: "app");

            migrationBuilder.DropTable(
                name: "family_team_invitations",
                schema: "app");

            migrationBuilder.DropTable(
                name: "family_team_memberships",
                schema: "app");

            migrationBuilder.DropTable(
                name: "family_teams",
                schema: "app");
        }
    }
}
