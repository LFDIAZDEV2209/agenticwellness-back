using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMotor365AiDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_plan_decision",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    person_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    engine_ver = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    params_version = table.Column<int>(type: "integer", nullable: false),
                    base_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    reason_es = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    reason_en = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    proposed = table.Column<string>(type: "jsonb", nullable: true),
                    reverts = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_plan_decision", x => x.id);
                    table.CheckConstraint("ck_motor_ai_day", "day BETWEEN 1 AND 365");
                    table.CheckConstraint("ck_motor_ai_status", "(status = 'applied' AND proposed IS NOT NULL AND code IS NULL AND reverts IS NULL) OR\n(status IN ('rejected', 'unavailable') AND code IS NOT NULL AND reverts IS NULL) OR\n(status = 'reverted' AND reverts IS NOT NULL AND proposed IS NULL AND code IS NULL)");
                    table.ForeignKey(
                        name: "FK_ai_plan_decision_ai_plan_decision_reverts",
                        column: x => x.reverts,
                        principalSchema: "app",
                        principalTable: "ai_plan_decision",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ai_plan_decision_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "app",
                        principalTable: "person",
                        principalColumn: "patient_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_plan_decision_person_id_day_sequence",
                schema: "app",
                table: "ai_plan_decision",
                columns: new[] { "person_id", "day", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_plan_decision_reverts",
                schema: "app",
                table: "ai_plan_decision",
                column: "reverts",
                unique: true);
            migrationBuilder.Sql("""
                CREATE TRIGGER motor365_ai_decision_append_only
                BEFORE UPDATE OR DELETE OR TRUNCATE ON app.ai_plan_decision
                FOR EACH STATEMENT EXECUTE FUNCTION app.motor365_reject_audit_mutation();
                REVOKE UPDATE, DELETE, TRUNCATE ON TABLE app.ai_plan_decision FROM PUBLIC;
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'app_user') THEN
                        REVOKE UPDATE, DELETE, TRUNCATE ON TABLE app.ai_plan_decision FROM app_user;
                        GRANT SELECT, INSERT ON TABLE app.ai_plan_decision TO app_user;
                        GRANT USAGE ON SEQUENCE app.ai_plan_decision_sequence_seq TO app_user;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_plan_decision",
                schema: "app");
        }
    }
}
