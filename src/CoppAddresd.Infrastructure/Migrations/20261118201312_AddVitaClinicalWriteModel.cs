using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVitaClinicalWriteModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El default constante evita reescribir las filas existentes.
            // Las columnas se pueden volver a aplicar si se interrumpe un índice concurrente.
            migrationBuilder.Sql("""
                ALTER TABLE app.clinical_measurements
                    ADD COLUMN IF NOT EXISTS idempotency_key text,
                    ADD COLUMN IF NOT EXISTS reported_by varchar(16),
                    ADD COLUMN IF NOT EXISTS source_channel varchar(24),
                    ADD COLUMN IF NOT EXISTS status varchar(24) NOT NULL DEFAULT 'confirmed',
                    ADD COLUMN IF NOT EXISTS supersedes_id uuid;
                """);

            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS app.ix_clinical_measurements_supersedes_id;",
                suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY ix_clinical_measurements_supersedes_id
                    ON app.clinical_measurements (supersedes_id) WHERE supersedes_id IS NOT NULL;
                """, suppressTransaction: true);
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS app.ux_clinical_measurements_patient_idempotency;",
                suppressTransaction: true);
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX CONCURRENTLY ux_clinical_measurements_patient_idempotency
                    ON app.clinical_measurements (patient_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
                """, suppressTransaction: true);

            // Agregar las restricciones con un bloqueo corto y validar por separado.
            migrationBuilder.Sql("""
                DO $vita$
                DECLARE target regclass := 'app.clinical_measurements'::regclass;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = target AND conname = 'ck_clinical_measurements_reported_by') THEN
                        ALTER TABLE app.clinical_measurements ADD CONSTRAINT ck_clinical_measurements_reported_by
                            CHECK (reported_by IN ('patient', 'professional', 'device')) NOT VALID;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = target AND conname = 'ck_clinical_measurements_source_channel') THEN
                        ALTER TABLE app.clinical_measurements ADD CONSTRAINT ck_clinical_measurements_source_channel
                            CHECK (source_channel IN ('vita_chat', 'vita_voice', 'vita_whatsapp', 'app_manual', 'device', 'professional', 'lab')) NOT VALID;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = target AND conname = 'ck_clinical_measurements_status') THEN
                        ALTER TABLE app.clinical_measurements ADD CONSTRAINT ck_clinical_measurements_status
                            CHECK (status IN ('confirmed', 'pending_confirmation', 'superseded')) NOT VALID;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = target AND conname = 'fk_clinical_measurements_supersedes') THEN
                        ALTER TABLE app.clinical_measurements ADD CONSTRAINT fk_clinical_measurements_supersedes
                            FOREIGN KEY (supersedes_id) REFERENCES app.clinical_measurements (id) NOT VALID;
                    END IF;
                END
                $vita$;
                """);
            migrationBuilder.Sql("""
                ALTER TABLE app.clinical_measurements
                    VALIDATE CONSTRAINT ck_clinical_measurements_reported_by,
                    VALIDATE CONSTRAINT ck_clinical_measurements_source_channel,
                    VALIDATE CONSTRAINT ck_clinical_measurements_status,
                    VALIDATE CONSTRAINT fk_clinical_measurements_supersedes;
                """, suppressTransaction: true);

            migrationBuilder.CreateTable(
                name: "vita_write_receipts",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    turn_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    tool_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vita_write_receipts", x => x.id);
                    table.CheckConstraint("ck_vita_write_receipts_channel", "channel IN ('vita_chat', 'vita_voice', 'vita_whatsapp')");
                    table.ForeignKey(
                        name: "FK_vita_write_receipts_patient_profiles_patient_id",
                        column: x => x.patient_id,
                        principalSchema: "app",
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_vita_write_receipts_patient_channel_turn_tool",
                schema: "app",
                table: "vita_write_receipts",
                columns: new[] { "patient_id", "channel", "turn_id", "tool_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_clinical_measurements_supersedes",
                schema: "app",
                table: "clinical_measurements");

            migrationBuilder.DropTable(
                name: "vita_write_receipts",
                schema: "app");

            migrationBuilder.DropIndex(
                name: "ix_clinical_measurements_supersedes_id",
                schema: "app",
                table: "clinical_measurements");

            migrationBuilder.DropIndex(
                name: "ux_clinical_measurements_patient_idempotency",
                schema: "app",
                table: "clinical_measurements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_clinical_measurements_reported_by",
                schema: "app",
                table: "clinical_measurements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_clinical_measurements_source_channel",
                schema: "app",
                table: "clinical_measurements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_clinical_measurements_status",
                schema: "app",
                table: "clinical_measurements");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                schema: "app",
                table: "clinical_measurements");

            migrationBuilder.DropColumn(
                name: "reported_by",
                schema: "app",
                table: "clinical_measurements");

            migrationBuilder.DropColumn(
                name: "source_channel",
                schema: "app",
                table: "clinical_measurements");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "app",
                table: "clinical_measurements");

            migrationBuilder.DropColumn(
                name: "supersedes_id",
                schema: "app",
                table: "clinical_measurements");
        }
    }
}
