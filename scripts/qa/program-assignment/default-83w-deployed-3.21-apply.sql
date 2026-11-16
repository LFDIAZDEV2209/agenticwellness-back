\set ON_ERROR_STOP on
-- PREPARADO, NO EJECUTADO. Requiere aprobación del orquestador y lectura RDS fresca.
-- Ejecutar psql desde un directorio NUEVO y vacío: backup.csv no debe existir.
-- El respaldo se exporta al cliente antes de cualquiera de los seis UPDATE.
BEGIN TRANSACTION ISOLATION LEVEL SERIALIZABLE;
SET LOCAL TIME ZONE 'UTC';
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';
SET LOCAL idle_in_transaction_session_timeout = '60s';

SELECT id FROM app.program_templates
WHERE code IN ('default-83w','program-coppaddresd-83-days') ORDER BY id FOR UPDATE;
SELECT d.id FROM app.weekly_day_templates d JOIN app.program_templates t ON t.id=d.template_id
WHERE t.code IN ('default-83w','program-coppaddresd-83-days') AND d.task_code='podcast'
ORDER BY d.id FOR UPDATE OF d;
SELECT m.id FROM app.media_items m WHERE m.id IN (
  SELECT d.media_id FROM app.weekly_day_templates d JOIN app.program_templates t ON t.id=d.template_id
  WHERE t.code IN ('default-83w','program-coppaddresd-83-days') AND d.task_code='podcast'
) ORDER BY m.id FOR SHARE;

CREATE TEMP TABLE repair_321_backup (payload jsonb NOT NULL) ON COMMIT DROP;
INSERT INTO repair_321_backup
SELECT jsonb_build_object(
  'operation','3.21-default-83w-six-media-refs', 'capturedAt',clock_timestamp(),
  'header',to_jsonb(t), 'sourceHeader',to_jsonb(s),
  'beforeRows',(SELECT jsonb_agg(to_jsonb(d) ORDER BY d.weekday,d.id)
    FROM app.weekly_day_templates d WHERE d.template_id=t.id AND d.task_code='podcast'),
  'afterRows',(SELECT jsonb_agg(CASE WHEN d.weekday BETWEEN 1 AND 6
    THEN jsonb_set(to_jsonb(d),'{media_id}',to_jsonb(a.media_id)) ELSE to_jsonb(d) END ORDER BY d.weekday,d.id)
    FROM app.weekly_day_templates d LEFT JOIN app.weekly_day_templates a
      ON a.template_id=s.id AND a.task_code='podcast' AND a.week_number IS NULL AND a.weekday=d.weekday
    WHERE d.template_id=t.id AND d.task_code='podcast'),
  'sourceRows',(SELECT jsonb_agg(jsonb_build_object('row',to_jsonb(d),'media',to_jsonb(m)) ORDER BY d.weekday,d.id)
    FROM app.weekly_day_templates d JOIN app.media_items m ON m.id=d.media_id
    WHERE d.template_id=s.id AND d.task_code='podcast' AND d.week_number IS NULL),
  'oldMedia',(SELECT jsonb_agg(to_jsonb(m) ORDER BY m.id) FROM app.media_items m WHERE m.id IN (
    SELECT d.media_id FROM app.weekly_day_templates d WHERE d.template_id=t.id AND d.task_code='podcast')),
  'activeEnrollments',(SELECT count(*) FROM app.program_enrollments e WHERE e.template_id=t.id AND e.status='Active')
)
FROM app.program_templates t CROSS JOIN app.program_templates s
WHERE t.code='default-83w' AND s.code='program-coppaddresd-83-days';

DO $guard$
DECLARE b jsonb;
BEGIN
  IF (SELECT count(*) FROM repair_321_backup) <> 1 THEN RAISE EXCEPTION 'Plantillas ambiguas/ausentes'; END IF;
  SELECT payload INTO b FROM repair_321_backup;
  IF b#>>'{header,status}' <> 'Archived' OR (b#>>'{header,total_weeks}')::int <> 83
    OR (b#>>'{header,version}')::int <> 3
    OR b#>>'{sourceHeader,status}' <> 'Active' OR (b#>>'{sourceHeader,total_weeks}')::int <> 12
    OR (b#>>'{sourceHeader,version}')::int <> 4
    OR (b->>'activeEnrollments')::int <> 116
    OR jsonb_array_length(b->'beforeRows') <> 7 OR jsonb_array_length(b->'afterRows') <> 7
    OR jsonb_array_length(b->'sourceRows') <> 7
  THEN RAISE EXCEPTION 'Cambió el alcance esperado: revisar, no forzar'; END IF;
  IF (SELECT count(DISTINCT (r->>'weekday')::int) FROM jsonb_array_elements(b->'beforeRows') r
      WHERE r->>'week_number' IS NULL AND (r->>'weekday')::int BETWEEN 1 AND 7) <> 7
  THEN RAISE EXCEPTION 'Se requieren siete filas genéricas ISO'; END IF;
  IF (SELECT count(*) FROM jsonb_array_elements(b->'sourceRows') r
      WHERE r#>>'{media,status}'='Published' AND r#>>'{media,media_type}'='Podcast'
        AND NULLIF(trim(r#>>'{media,storage_key}'),'') IS NOT NULL
        AND lower(trim(r#>>'{media,storage_key}')) <> 'media/podcasts/demo-copp.mp3') <> 7
  THEN RAISE EXCEPTION 'Respaldo activo no válido'; END IF;
  IF (SELECT count(DISTINCT (r#>>'{row,weekday}')::int) FROM jsonb_array_elements(b->'sourceRows') r
      WHERE (r#>>'{row,weekday}')::int BETWEEN 1 AND 7) <> 7
    OR EXISTS (SELECT 1 FROM app.weekly_day_templates d
      WHERE d.template_id=(b#>>'{sourceHeader,id}')::uuid AND d.task_code='podcast' AND d.week_number IS NOT NULL)
  THEN RAISE EXCEPTION 'Cambió la cuadrícula activa observada'; END IF;
  IF (SELECT count(*) FROM jsonb_array_elements(b->'beforeRows') r
      JOIN app.media_items m ON m.id=(r->>'media_id')::uuid
      WHERE (r->>'weekday')::int BETWEEN 1 AND 6 AND m.storage_key='media/podcasts/demo-copp.mp3') <> 6
  THEN RAISE EXCEPTION 'No hay exactamente seis referencias demo lunes-sábado'; END IF;
  -- Preimagen confirmada por READ ONLY RDS vía ECS cooppadresd-dbops:4 (16-11).
  IF (SELECT count(*) FROM jsonb_array_elements(b->'beforeRows') r
      JOIN (VALUES
        (1,'e7c3979b-5dbe-4b9c-a77d-7b8014b0a355'),
        (2,'66d8b715-bfae-4545-bbd9-ecc4715060c6'),
        (3,'be217da4-6b1f-4c4c-9b00-67bf940fcaf1'),
        (4,'7a23f517-b71f-44cf-9cea-a1e2a8bcd44d'),
        (5,'ab682ed6-f6c6-49cf-a064-c5eec53ce3fb'),
        (6,'ec47f53d-fe77-42c1-b772-2418e39e5cf0'),
        (7,'ed1e3490-1e91-41a9-ba17-611941b0dd31')
      ) expected(weekday,media_id) ON expected.weekday=(r->>'weekday')::int AND expected.media_id=r->>'media_id') <> 7
  THEN RAISE EXCEPTION 'Cambió el mapeo legacy observado en RDS'; END IF;
  IF NOT EXISTS (SELECT 1 FROM jsonb_array_elements(b->'beforeRows') r JOIN app.media_items m ON m.id=(r->>'media_id')::uuid
      WHERE (r->>'weekday')::int=7 AND m.status='Published' AND m.media_type='Podcast'
      AND NULLIF(trim(m.storage_key),'') IS NOT NULL AND lower(trim(m.storage_key))<>'media/podcasts/demo-copp.mp3')
  THEN RAISE EXCEPTION 'Domingo dejó de ser válido'; END IF;
END $guard$;

-- CSV de una columna JSONB, sin pacientes ni credenciales. Conservar para reverse.sql.
\copy repair_321_backup(payload) TO 'backup.csv' WITH (FORMAT csv)

DO $repair$
DECLARE b jsonb; n int; total int := 0;
BEGIN
  SELECT payload INTO b FROM repair_321_backup;
  -- Solo media_id; IDs/referencias proceden del respaldo bloqueado, nunca de datos locales.
  UPDATE app.weekly_day_templates d SET media_id=(a->>'media_id')::uuid
    FROM jsonb_array_elements(b->'beforeRows') old, jsonb_array_elements(b->'afterRows') a
    WHERE d.id=(old->>'id')::uuid AND a->>'id'=old->>'id' AND d.weekday=1 AND to_jsonb(d)=old;
  GET DIAGNOSTICS n=ROW_COUNT; total:=total+n;
  UPDATE app.weekly_day_templates d SET media_id=(a->>'media_id')::uuid
    FROM jsonb_array_elements(b->'beforeRows') old, jsonb_array_elements(b->'afterRows') a
    WHERE d.id=(old->>'id')::uuid AND a->>'id'=old->>'id' AND d.weekday=2 AND to_jsonb(d)=old;
  GET DIAGNOSTICS n=ROW_COUNT; total:=total+n;
  UPDATE app.weekly_day_templates d SET media_id=(a->>'media_id')::uuid
    FROM jsonb_array_elements(b->'beforeRows') old, jsonb_array_elements(b->'afterRows') a
    WHERE d.id=(old->>'id')::uuid AND a->>'id'=old->>'id' AND d.weekday=3 AND to_jsonb(d)=old;
  GET DIAGNOSTICS n=ROW_COUNT; total:=total+n;
  UPDATE app.weekly_day_templates d SET media_id=(a->>'media_id')::uuid
    FROM jsonb_array_elements(b->'beforeRows') old, jsonb_array_elements(b->'afterRows') a
    WHERE d.id=(old->>'id')::uuid AND a->>'id'=old->>'id' AND d.weekday=4 AND to_jsonb(d)=old;
  GET DIAGNOSTICS n=ROW_COUNT; total:=total+n;
  UPDATE app.weekly_day_templates d SET media_id=(a->>'media_id')::uuid
    FROM jsonb_array_elements(b->'beforeRows') old, jsonb_array_elements(b->'afterRows') a
    WHERE d.id=(old->>'id')::uuid AND a->>'id'=old->>'id' AND d.weekday=5 AND to_jsonb(d)=old;
  GET DIAGNOSTICS n=ROW_COUNT; total:=total+n;
  UPDATE app.weekly_day_templates d SET media_id=(a->>'media_id')::uuid
    FROM jsonb_array_elements(b->'beforeRows') old, jsonb_array_elements(b->'afterRows') a
    WHERE d.id=(old->>'id')::uuid AND a->>'id'=old->>'id' AND d.weekday=6 AND to_jsonb(d)=old;
  GET DIAGNOSTICS n=ROW_COUNT; total:=total+n;
  IF total<>6 OR (SELECT jsonb_agg(to_jsonb(d) ORDER BY d.weekday,d.id) FROM app.weekly_day_templates d
      WHERE d.template_id=(b#>>'{header,id}')::uuid AND d.task_code='podcast') IS DISTINCT FROM b->'afterRows'
    OR (SELECT to_jsonb(t) FROM app.program_templates t WHERE id=(b#>>'{header,id}')::uuid) IS DISTINCT FROM b->'header'
  THEN RAISE EXCEPTION 'CAS/postimagen inválida: rollback completo'; END IF;
END $repair$;
COMMIT;
