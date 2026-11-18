using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoppAddresd.Infrastructure.Migrations;

// El mapping EF conserva el contrato de lectura y escritura de la relación.
[DbContext(typeof(AppDbContext))]
[Migration("20261118214000_ConsolidateLegacyVitalSigns")]
public sealed class ConsolidateLegacyVitalSigns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            -- Impide escrituras durante la copia. Los lectores siguen activos hasta el renombre.
            LOCK TABLE app.vital_signs IN SHARE ROW EXCLUSIVE MODE;

            CREATE FUNCTION app.expand_vital_signs(numeric, numeric, integer, integer, integer, numeric, integer)
            RETURNS TABLE (code text, unit_code text, reading numeric)
            LANGUAGE sql IMMUTABLE
            SET search_path = pg_catalog
            AS $expand$
                SELECT * FROM (VALUES
                    ('weight', 'kg', $1),
                    ('height', 'cm', $2),
                    ('systolic_bp', 'mmhg', $3::numeric),
                    ('diastolic_bp', 'mmhg', $4::numeric),
                    ('heart_rate', 'bpm', $5::numeric),
                    ('temperature_c', 'celsius', $6),
                    ('o2_saturation', 'pct', $7::numeric)
                ) AS supported(code, unit_code, reading)
            $expand$;

            DO $verify$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM app.expand_vital_signs(NULL, NULL, NULL, NULL, NULL, NULL, NULL) x
                    LEFT JOIN app.measurement_metrics m ON m.code = x.code
                    LEFT JOIN app.unit_of_measures u ON u.code = x.unit_code
                    WHERE m.id IS NULL OR u.id IS NULL
                ) THEN
                    RAISE EXCEPTION 'Falta parte del catálogo requerido para consolidar signos vitales';
                END IF;
            END
            $verify$;

            INSERT INTO app.clinical_measurements
                (id, patient_id, batch_id, metric_id, unit_id, value, observed_at, recorded_at, created_at,
                 source, status, idempotency_key)
            SELECT md5(k.key)::uuid, v.patient_id, v.id, m.id, u.id, x.reading, v.measured_at, v.created_at, v.created_at,
                'legacy', 'confirmed', k.key
            FROM app.vital_signs v
            CROSS JOIN LATERAL app.expand_vital_signs(v.weight_kg, v.height_cm, v.systolic, v.diastolic,
                v.heart_rate, v.temperature_c, v.o2_saturation) x
            JOIN app.measurement_metrics m ON m.code = x.code
            JOIN app.unit_of_measures u ON u.code = x.unit_code
            CROSS JOIN LATERAL (SELECT 'legacy-vitals:' || v.id || ':' || x.code AS key) k
            WHERE x.reading IS NOT NULL
            ON CONFLICT (patient_id, idempotency_key) WHERE idempotency_key IS NOT NULL DO NOTHING;

            DO $verify$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM app.vital_signs v
                    CROSS JOIN LATERAL app.expand_vital_signs(v.weight_kg, v.height_cm, v.systolic, v.diastolic,
                        v.heart_rate, v.temperature_c, v.o2_saturation) x
                    JOIN app.measurement_metrics m ON m.code = x.code
                    JOIN app.unit_of_measures u ON u.code = x.unit_code
                    WHERE x.reading IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM app.clinical_measurements c
                        WHERE c.patient_id = v.patient_id AND c.idempotency_key = 'legacy-vitals:' || v.id || ':' || x.code
                          AND c.batch_id = v.id AND c.source = 'legacy' AND c.source_key IS NULL
                          AND c.metric_id = m.id AND c.unit_id = u.id AND c.value = x.reading
                          AND c.observed_at = v.measured_at AND c.recorded_at = v.created_at AND c.created_at = v.created_at
                          AND c.status = 'confirmed' AND c.reported_by IS NULL AND c.source_channel IS NULL
                    )
                ) THEN
                    RAISE EXCEPTION 'Hay signos vitales sin una medición canónica equivalente';
                END IF;
            END
            $verify$;

            ALTER TABLE app.vital_signs RENAME TO vital_signs_legacy;
            REVOKE SELECT, INSERT, UPDATE, DELETE ON app.vital_signs_legacy FROM app_user;

            -- El respaldo conserva id y fechas incluso si la toma tiene todos sus valores nulos.
            -- Para una consulta por paciente se busca por el índice original y luego por batch_id.
            CREATE VIEW app.vital_signs AS
            SELECT v.id, v.patient_id, v.measured_at, c.systolic, c.diastolic, c.heart_rate,
                c.temperature_c, c.o2_saturation, c.height_cm, c.weight_kg, v.created_at
            FROM app.vital_signs_legacy v
            CROSS JOIN LATERAL (
                SELECT
                    max(cm.value) FILTER (WHERE m.code = 'systolic_bp')::integer AS systolic,
                    max(cm.value) FILTER (WHERE m.code = 'diastolic_bp')::integer AS diastolic,
                    max(cm.value) FILTER (WHERE m.code = 'heart_rate')::integer AS heart_rate,
                    max(cm.value) FILTER (WHERE m.code = 'temperature_c')::numeric(4,1) AS temperature_c,
                    max(cm.value) FILTER (WHERE m.code = 'o2_saturation')::integer AS o2_saturation,
                    max(cm.value) FILTER (WHERE m.code = 'height')::numeric(6,1) AS height_cm,
                    max(cm.value) FILTER (WHERE m.code = 'weight')::numeric(6,1) AS weight_kg
                FROM app.clinical_measurements cm
                JOIN app.measurement_metrics m ON m.id = cm.metric_id
                WHERE cm.batch_id = v.id AND cm.patient_id = v.patient_id
                  AND cm.source = 'legacy' AND cm.idempotency_key LIKE 'legacy-vitals:%'
            ) c;
            ALTER VIEW app.vital_signs ALTER COLUMN id SET DEFAULT gen_random_uuid();
            ALTER VIEW app.vital_signs ALTER COLUMN created_at SET DEFAULT now();
            GRANT SELECT, INSERT, UPDATE, DELETE ON app.vital_signs TO app_user;

            -- Mantiene la escritura de expedientes e importadores por el nombre anterior.
            -- Las nuevas tomas guardan solo su cabecera en la tabla renombrada.
            CREATE FUNCTION app.write_vital_signs_view() RETURNS trigger
            LANGUAGE plpgsql SECURITY DEFINER
            SET search_path = pg_catalog
            AS $write$
            BEGIN
                IF TG_OP = 'INSERT' THEN
                    INSERT INTO app.vital_signs_legacy (id, patient_id, measured_at, created_at)
                    VALUES (NEW.id, NEW.patient_id, NEW.measured_at, NEW.created_at);
                ELSE
                    IF TG_OP = 'DELETE' THEN
                        DELETE FROM app.vital_signs_legacy WHERE id = OLD.id;
                    ELSE
                        UPDATE app.vital_signs_legacy
                        SET id = NEW.id, patient_id = NEW.patient_id, measured_at = NEW.measured_at, created_at = NEW.created_at
                        WHERE id = OLD.id;
                    END IF;
                    IF NOT FOUND THEN RETURN NULL; END IF;

                    DELETE FROM app.clinical_measurements
                    WHERE batch_id = OLD.id AND patient_id = OLD.patient_id
                      AND source = 'legacy' AND idempotency_key LIKE 'legacy-vitals:%';
                    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                END IF;

                INSERT INTO app.clinical_measurements
                    (id, patient_id, batch_id, metric_id, unit_id, value, observed_at, recorded_at, created_at,
                     source, status, idempotency_key)
                SELECT md5(k.key)::uuid, NEW.patient_id, NEW.id, m.id, u.id, x.reading, NEW.measured_at,
                    NEW.created_at, NEW.created_at, 'legacy', 'confirmed', k.key
                FROM app.expand_vital_signs(NEW.weight_kg, NEW.height_cm, NEW.systolic, NEW.diastolic,
                    NEW.heart_rate, NEW.temperature_c, NEW.o2_saturation) x
                LEFT JOIN app.measurement_metrics m ON m.code = x.code
                LEFT JOIN app.unit_of_measures u ON u.code = x.unit_code
                CROSS JOIN LATERAL (SELECT 'legacy-vitals:' || NEW.id || ':' || x.code AS key) k
                WHERE x.reading IS NOT NULL;
                RETURN NEW;
            END
            $write$;
            REVOKE EXECUTE ON FUNCTION app.write_vital_signs_view() FROM PUBLIC;
            CREATE TRIGGER vital_signs_compat_write
                INSTEAD OF INSERT OR UPDATE OR DELETE ON app.vital_signs
                FOR EACH ROW EXECUTE FUNCTION app.write_vital_signs_view();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            LOCK TABLE app.vital_signs IN ACCESS EXCLUSIVE MODE;

            -- Rehidrata también altas y ediciones hechas mientras existió la vista.
            UPDATE app.vital_signs_legacy v
            SET systolic = current.systolic, diastolic = current.diastolic, heart_rate = current.heart_rate,
                temperature_c = current.temperature_c, o2_saturation = current.o2_saturation,
                height_cm = current.height_cm, weight_kg = current.weight_kg
            FROM app.vital_signs current WHERE current.id = v.id
              AND ROW(v.systolic, v.diastolic, v.heart_rate, v.temperature_c, v.o2_saturation, v.height_cm, v.weight_kg)
                  IS DISTINCT FROM ROW(current.systolic, current.diastolic, current.heart_rate, current.temperature_c,
                      current.o2_saturation, current.height_cm, current.weight_kg);

            DROP VIEW app.vital_signs;
            DROP FUNCTION app.write_vital_signs_view();
            DROP FUNCTION app.expand_vital_signs(numeric, numeric, integer, integer, integer, numeric, integer);
            DELETE FROM app.clinical_measurements
            WHERE source = 'legacy' AND idempotency_key LIKE 'legacy-vitals:%';
            ALTER TABLE app.vital_signs_legacy RENAME TO vital_signs;
            GRANT SELECT, INSERT, UPDATE, DELETE ON app.vital_signs TO app_user;
            """);
    }
}
