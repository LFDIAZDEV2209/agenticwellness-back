using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoppAddresd.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261118223000_AddVitaDocumentIndexOutbox")]
public sealed class AddVitaDocumentIndexOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE app.vita_document_index_outbox (
            document_id uuid NOT NULL,
            patient_id uuid NOT NULL,
            generation uuid NOT NULL,
            next_attempt_at timestamptz NOT NULL,
            attempts integer NOT NULL DEFAULT 0,
            claim_token uuid,
            lease_until timestamptz,
            PRIMARY KEY (document_id, patient_id)
        );
        CREATE INDEX ix_vita_document_index_outbox_due
            ON app.vita_document_index_outbox (next_attempt_at, document_id, patient_id);
        GRANT SELECT, UPDATE, DELETE ON app.vita_document_index_outbox TO app_user;

        CREATE FUNCTION app.queue_vita_document_index() RETURNS trigger
        LANGUAGE plpgsql SECURITY DEFINER
        SET search_path = pg_catalog
        AS $queue$
        DECLARE
            changed_document uuid;
            owner_id uuid;
            previous_owner uuid;
            refresh_versions boolean := false;
        BEGIN
            IF TG_OP = 'DELETE' THEN
                changed_document := OLD.id;
                owner_id := OLD.patient_id;
            ELSE
                changed_document := NEW.id;
                owner_id := NEW.patient_id;
                IF TG_OP = 'UPDATE' THEN
                    IF ROW(OLD.patient_id, OLD.title, OLD.status, OLD.deleted_at, OLD.storage_key,
                           OLD.content_type, OLD.file_size_bytes, OLD.created_at, OLD.parent_document_id)
                        IS NOT DISTINCT FROM
                       ROW(NEW.patient_id, NEW.title, NEW.status, NEW.deleted_at, NEW.storage_key,
                           NEW.content_type, NEW.file_size_bytes, NEW.created_at, NEW.parent_document_id) THEN
                        RETURN NULL;
                    END IF;
                    IF OLD.patient_id IS DISTINCT FROM NEW.patient_id THEN
                        previous_owner := OLD.patient_id;
                    END IF;
                    refresh_versions := NEW.parent_document_id IS NULL AND
                        ROW(OLD.status, OLD.deleted_at, OLD.patient_id)
                            IS DISTINCT FROM ROW(NEW.status, NEW.deleted_at, NEW.patient_id);
                END IF;
            END IF;

            INSERT INTO app.vita_document_index_outbox (document_id, patient_id, generation, next_attempt_at)
            SELECT document_id, patient_id, gen_random_uuid(), now()
            FROM (
                SELECT changed_document AS document_id, owner_id AS patient_id WHERE owner_id IS NOT NULL
                UNION ALL
                SELECT changed_document, previous_owner WHERE previous_owner IS NOT NULL
                UNION ALL
                SELECT id, patient_id FROM app.documents
                WHERE refresh_versions AND parent_document_id = changed_document AND patient_id IS NOT NULL
            ) affected
            ORDER BY document_id, patient_id
            ON CONFLICT (document_id, patient_id) DO UPDATE
            SET generation = EXCLUDED.generation, next_attempt_at = EXCLUDED.next_attempt_at, attempts = 0;
            RETURN NULL;
        END
        $queue$;
        REVOKE EXECUTE ON FUNCTION app.queue_vita_document_index() FROM PUBLIC;
        CREATE TRIGGER vita_document_index_after_change
            AFTER INSERT OR UPDATE OR DELETE ON app.documents
            FOR EACH ROW EXECUTE FUNCTION app.queue_vita_document_index();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TRIGGER vita_document_index_after_change ON app.documents;
        DROP FUNCTION app.queue_vita_document_index();
        DROP TABLE app.vita_document_index_outbox;
        """);
}
