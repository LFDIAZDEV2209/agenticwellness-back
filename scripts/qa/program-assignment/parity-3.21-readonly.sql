-- Diagnóstico desplegado 3.21: únicamente lectura, sin identificadores de pacientes.
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
SET LOCAL lock_timeout = '3s';
SELECT jsonb_build_object(
  'capturedAt', clock_timestamp(),
  'transactionReadOnly', current_setting('transaction_read_only'),
  'isolation', current_setting('transaction_isolation'),
  'templates', (
    SELECT jsonb_agg(jsonb_build_object(
      'header', to_jsonb(t),
      'activeEnrollments', (SELECT count(*) FROM app.program_enrollments e WHERE e.template_id=t.id AND e.status='Active'),
      'podcastRows', (SELECT jsonb_agg(jsonb_build_object('row', to_jsonb(d), 'media', to_jsonb(m)) ORDER BY d.week_number NULLS FIRST,d.weekday,d.id)
        FROM app.weekly_day_templates d LEFT JOIN app.media_items m ON m.id=d.media_id
        WHERE d.template_id=t.id AND d.task_code='podcast')
    ) ORDER BY t.code)
    FROM app.program_templates t WHERE t.code IN ('default-83w','program-coppaddresd-83-days')
  )
);
ROLLBACK;
