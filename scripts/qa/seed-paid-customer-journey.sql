\set ON_ERROR_STOP on

BEGIN;

DO $$
BEGIN
    IF current_database() !~ '^copp_review_' THEN
        RAISE EXCEPTION 'La prueba de activación requiere una base desechable copp_review_*';
    END IF;
END $$;

INSERT INTO erp.legal_documents (code, title)
VALUES ('paid-demo-privacy-es', 'Política de privacidad de prueba'),
       ('paid-demo-terms-es', 'Términos de prueba'),
       ('paid-demo-privacy-en', 'Test privacy policy'),
       ('paid-demo-terms-en', 'Test terms')
ON CONFLICT (code) DO NOTHING;

INSERT INTO erp.legal_document_versions (document_id, major, minor, is_published, content)
SELECT id, 1, 0, true,
       CASE WHEN right(code, 2) = 'es'
        THEN 'Documento sintético para verificar la activación del entorno de pruebas.'
        ELSE 'Synthetic document for the local activation acceptance test.'
       END
FROM erp.legal_documents
WHERE code IN ('paid-demo-privacy-es', 'paid-demo-terms-es', 'paid-demo-privacy-en', 'paid-demo-terms-en')
ON CONFLICT (document_id, major, minor) DO NOTHING;

UPDATE erp.legal_documents d
SET current_version_id = v.id
FROM erp.legal_document_versions v
WHERE d.id = v.document_id
  AND d.code IN ('paid-demo-privacy-es', 'paid-demo-terms-es', 'paid-demo-privacy-en', 'paid-demo-terms-en')
  AND d.current_version_id IS NULL
  AND v.major = 1 AND v.minor = 0 AND v.is_published;

INSERT INTO app.legal_document_acceptances (
    id, user_id, legal_document_version_id, scope, locale,
    disclosure_id, disclosure_snapshot, idempotency_key, accepted_at)
SELECT gen_random_uuid(), u."Id", d.current_version_id, d.code, right(d.code, 2),
       encode(sha256(convert_to(d.code || ':' || d.current_version_id::text, 'UTF8')), 'hex'),
       '{"source":"synthetic-existing-user"}'::jsonb, gen_random_uuid(), now()
FROM erp.legal_documents d
CROSS JOIN auth."Users" u
WHERE d.code IN ('paid-demo-privacy-es', 'paid-demo-terms-es', 'paid-demo-privacy-en', 'paid-demo-terms-en')
  AND u."NormalizedEmail" = upper(:'returning_email')
  AND NOT EXISTS (
    SELECT 1 FROM app.legal_document_acceptances a
    WHERE a.user_id = u."Id" AND a.legal_document_version_id = d.current_version_id
      AND a.scope = d.code AND a.withdrawn_at IS NULL
  );

COMMIT;
