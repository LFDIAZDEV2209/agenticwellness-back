-- Ejecutar sobre una copia autorizada con una conexión de solo lectura.
--   psql -X -qAt -f scripts/qa/kan-122-email-reconciliation-readonly.sql
-- Para una revisión autorizada, agregar -v kan122_include_ids=on. Ese modo
-- incluye UUID de Auth y señales de alcance. Tratar su salida como restringida.
-- Este inventario agrupa candidatos. La coincidencia no acredita titularidad.
--
-- stored_key usa exactamente el valor persistido por Identity. candidate_key
-- aproxima Trim + NFC + ToUpperInvariant sobre una base PostgreSQL UTF-8.
-- El recorte enumera el espacio Unicode de .NET. UPPER usa la colación de la
-- base y puede producir resultados distintos de .NET por idioma y versión.
-- Confirmar los candidatos con el ILookupNormalizer del Auth desplegado.

\set ON_ERROR_STOP on
\if :{?kan122_include_ids}
\else
    \set kan122_include_ids off
\endif

BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout = '30s';
SET LOCAL lock_timeout = '3s';

WITH trimmed_accounts AS (
    SELECT u."Id" AS user_id,
           u."EmailConfirmed" AS email_confirmed,
           u."IsActive" AS is_active,
           u."DeletionRequestedAt" IS NOT NULL AS deletion_requested,
           u."PurgedAt" IS NOT NULL AS is_purged,
           u."NormalizedEmail" AS stored_key,
           NULLIF(btrim(
               u."Email",
               U&'\0009\000A\000B\000C\000D\0020\0085\00A0\1680'
                   || U&'\2000\2001\2002\2003\2004\2005\2006\2007\2008\2009\200A\2028\2029\202F\205F\3000'
           ), '') AS trimmed_email
    FROM auth."Users" u
), accounts AS MATERIALIZED (
    SELECT user_id, email_confirmed, is_active, deletion_requested, is_purged,
           stored_key, upper(normalize(trimmed_email, NFC)) AS candidate_key
    FROM trimmed_accounts
), keys AS MATERIALIZED (
    SELECT 'stored' AS basis, stored_key AS email_key, user_id
    FROM accounts WHERE stored_key IS NOT NULL
    UNION ALL
    SELECT 'candidate', candidate_key, user_id
    FROM accounts WHERE candidate_key IS NOT NULL
), collisions AS MATERIALIZED (
    SELECT basis, email_key, count(*) AS member_count
    FROM keys
    GROUP BY basis, email_key
    HAVING count(*) > 1
), summary AS (
    SELECT count(*) AS total,
           count(*) FILTER (WHERE is_purged) AS purged,
           count(*) FILTER (WHERE candidate_key IS NULL) AS missing_email,
           count(*) FILTER (WHERE candidate_key IS NOT NULL AND NOT email_confirmed) AS unconfirmed_email,
           count(*) FILTER (WHERE candidate_key IS NOT NULL AND NULLIF(stored_key, '') IS NULL) AS missing_stored_key,
           count(*) FILTER (WHERE candidate_key IS NULL AND stored_key IS NOT NULL) AS stored_key_without_email,
           count(*) FILTER (WHERE candidate_key IS NOT NULL AND NULLIF(stored_key, '') IS NOT NULL
                                AND stored_key <> candidate_key) AS approximate_key_disagreements
    FROM accounts
), collision_totals AS (
    SELECT count(*) FILTER (WHERE basis = 'stored') AS stored_groups,
           coalesce(sum(member_count) FILTER (WHERE basis = 'stored'), 0) AS stored_accounts,
           count(*) FILTER (WHERE basis = 'candidate') AS candidate_groups,
           coalesce(sum(member_count) FILTER (WHERE basis = 'candidate'), 0) AS candidate_accounts
    FROM collisions
)
SELECT jsonb_build_object(
    'accounts', to_jsonb(summary),
    'collision_totals', to_jsonb(collision_totals),
    'collision_groups', CASE WHEN :'kan122_include_ids'::boolean THEN (
        SELECT coalesce(jsonb_agg(jsonb_build_object('basis', basis, 'members', members)
                                  ORDER BY basis, first_user_id), '[]'::jsonb)
        FROM (
            SELECT c.basis,
                   min(a.user_id::text) AS first_user_id,
                   jsonb_agg(jsonb_build_object(
                       'user_id', a.user_id,
                       'is_active', a.is_active,
                       'email_confirmed', a.email_confirmed,
                       'deletion_requested', a.deletion_requested,
                       'is_purged', a.is_purged,
                       'has_patient_link', EXISTS (
                           SELECT 1 FROM app.patient_profiles p WHERE p.user_id = a.user_id
                       ),
                       'has_employee_link', EXISTS (
                           SELECT 1 FROM erp.employees e WHERE e.user_id = a.user_id
                       ),
                       'app_grants', coalesce((
                           SELECT jsonb_object_agg(ap."Code", jsonb_build_object(
                               'application_active', ap."IsActive", 'grant_suspended', ua."IsSuspended"
                           ))
                           FROM auth."UserApplications" ua
                           JOIN auth."Applications" ap ON ap."Id" = ua."ApplicationId"
                           WHERE ua."UserId" = a.user_id AND ap."Code" IN ('app', 'erp')
                       ), '{}'::jsonb)
                   ) ORDER BY a.user_id) AS members
            FROM collisions c
            JOIN keys k ON k.basis = c.basis AND k.email_key = c.email_key
            JOIN accounts a ON a.user_id = k.user_id
            GROUP BY c.basis, c.email_key
        ) group_rows
    ) END
) AS reconciliation_inventory
FROM summary CROSS JOIN collision_totals;

ROLLBACK;
