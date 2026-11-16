\set ON_ERROR_STOP on
-- PREPARADO, NO EJECUTADO. Desde el directorio que contiene backup.csv del apply.
BEGIN TRANSACTION ISOLATION LEVEL SERIALIZABLE;
SET LOCAL TIME ZONE 'UTC';
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';
SET LOCAL idle_in_transaction_session_timeout = '60s';
CREATE TEMP TABLE repair_321_backup (payload jsonb NOT NULL) ON COMMIT DROP;
\copy repair_321_backup(payload) FROM 'backup.csv' WITH (FORMAT csv)

SELECT t.id FROM app.program_templates t JOIN repair_321_backup b ON t.id=(b.payload#>>'{header,id}')::uuid FOR UPDATE OF t;
SELECT d.id FROM app.weekly_day_templates d JOIN repair_321_backup b ON d.template_id=(b.payload#>>'{header,id}')::uuid
WHERE d.task_code='podcast' ORDER BY d.id FOR UPDATE OF d;
DO $reverse$
DECLARE b jsonb; n int; total int := 0;
BEGIN
  IF (SELECT count(*) FROM repair_321_backup)<>1 THEN RAISE EXCEPTION 'Respaldo ambiguo'; END IF;
  SELECT payload INTO b FROM repair_321_backup;
  IF b->>'operation' IS DISTINCT FROM '3.21-default-83w-six-media-refs'
    OR b#>>'{header,code}' IS DISTINCT FROM 'default-83w'
    OR jsonb_array_length(b->'beforeRows') IS DISTINCT FROM 7
    OR jsonb_array_length(b->'afterRows') IS DISTINCT FROM 7
    OR (SELECT to_jsonb(t) FROM app.program_templates t WHERE id=(b#>>'{header,id}')::uuid) IS DISTINCT FROM b->'header'
    OR (SELECT jsonb_agg(to_jsonb(d) ORDER BY d.weekday,d.id) FROM app.weekly_day_templates d
      WHERE d.template_id=(b#>>'{header,id}')::uuid AND d.task_code='podcast') IS DISTINCT FROM b->'afterRows'
  THEN RAISE EXCEPTION 'Cambió cabecera/postimagen; no sobrescribir ediciones posteriores'; END IF;
  -- El respaldo debe restaurar exclusivamente media_id en seis filas.
  IF (SELECT count(*) FROM jsonb_array_elements(b->'beforeRows') old JOIN jsonb_array_elements(b->'afterRows') a
      ON old->>'id'=a->>'id' WHERE old-'media_id'=a-'media_id' AND old->>'media_id'<>a->>'media_id'
      AND (old->>'weekday')::int BETWEEN 1 AND 6)<>6
  THEN RAISE EXCEPTION 'El respaldo no describe seis cambios de media_id'; END IF;
  UPDATE app.weekly_day_templates d SET media_id=(old->>'media_id')::uuid
    FROM jsonb_array_elements(b->'beforeRows') old WHERE d.id=(old->>'id')::uuid AND d.weekday=1;
  GET DIAGNOSTICS n=ROW_COUNT; total:=total+n;
  UPDATE app.weekly_day_templates d SET media_id=(old->>'media_id')::uuid
    FROM jsonb_array_elements(b->'beforeRows') old WHERE d.id=(old->>'id')::uuid AND d.weekday=2;
  GET DIAGNOSTICS n=ROW_COUNT; total:=total+n;
  UPDATE app.weekly_day_templates d SET media_id=(old->>'media_id')::uuid
    FROM jsonb_array_elements(b->'beforeRows') old WHERE d.id=(old->>'id')::uuid AND d.weekday=3;
  GET DIAGNOSTICS n=ROW_COUNT; total:=total+n;
  UPDATE app.weekly_day_templates d SET media_id=(old->>'media_id')::uuid
    FROM jsonb_array_elements(b->'beforeRows') old WHERE d.id=(old->>'id')::uuid AND d.weekday=4;
  GET DIAGNOSTICS n=ROW_COUNT; total:=total+n;
  UPDATE app.weekly_day_templates d SET media_id=(old->>'media_id')::uuid
    FROM jsonb_array_elements(b->'beforeRows') old WHERE d.id=(old->>'id')::uuid AND d.weekday=5;
  GET DIAGNOSTICS n=ROW_COUNT; total:=total+n;
  UPDATE app.weekly_day_templates d SET media_id=(old->>'media_id')::uuid
    FROM jsonb_array_elements(b->'beforeRows') old WHERE d.id=(old->>'id')::uuid AND d.weekday=6;
  GET DIAGNOSTICS n=ROW_COUNT; total:=total+n;
  IF total<>6 OR (SELECT jsonb_agg(to_jsonb(d) ORDER BY d.weekday,d.id) FROM app.weekly_day_templates d
      WHERE d.template_id=(b#>>'{header,id}')::uuid AND d.task_code='podcast') IS DISTINCT FROM b->'beforeRows'
  THEN RAISE EXCEPTION 'Reversa incompleta: rollback completo'; END IF;
END $reverse$;
COMMIT;
