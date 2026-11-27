-- Ejecutar solo en el PostgreSQL efímero de la prueba Playwright.
\set ON_ERROR_STOP on
BEGIN;
DO $$ BEGIN
    IF current_database() <> 'copp_review_kan170' THEN
        RAISE EXCEPTION 'Base incorrecta: se requiere el entorno sintético copp_review_kan170';
    END IF;
END $$;

INSERT INTO auth."Users"
    ("Id", "FirstName", "LastName", "IsActive", "CreatedAt", "EmailConfirmed",
     "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
VALUES
    ('17000000-0000-4000-8000-000000000001', 'Privacy', 'Synthetic QA', true, now(), false, false, false, false, 0),
    ('17000000-0000-4000-8000-000000000002', 'Privacy', 'Synthetic QA', true, now(), false, false, false, false, 0),
    ('17000000-0000-4000-8000-000000000003', 'Privacy', 'Synthetic QA', false, now(), false, false, false, false, 0)
ON CONFLICT DO NOTHING;

INSERT INTO auth."UserApplications" ("UserId", "ApplicationId", "CreatedAt")
SELECT u."Id", a."Id", now()
FROM auth."Users" u CROSS JOIN auth."Applications" a
WHERE u."Id" IN ('17000000-0000-4000-8000-000000000001', '17000000-0000-4000-8000-000000000002',
                 '17000000-0000-4000-8000-000000000003') AND a."Code" = 'app'
ON CONFLICT DO NOTHING;

-- El segundo usuario prueba, además, publicación ERP con un sello sintético.
UPDATE auth."Users" SET "SecurityStamp" = 'kan170-synthetic-stamp'
WHERE "Id" = '17000000-0000-4000-8000-000000000002';

INSERT INTO auth."UserApplications" ("UserId", "ApplicationId", "CreatedAt")
SELECT '17000000-0000-4000-8000-000000000002', "Id", now()
FROM auth."Applications" WHERE "Code" = 'erp'
ON CONFLICT DO NOTHING;

INSERT INTO auth."UserPreferences" ("UserId", "Lang", "UpdatedAt")
VALUES ('17000000-0000-4000-8000-000000000001', 'es', now()),
       ('17000000-0000-4000-8000-000000000002', 'en', now())
ON CONFLICT ("UserId") DO UPDATE SET "Lang" = EXCLUDED."Lang", "UpdatedAt" = EXCLUDED."UpdatedAt";

DO $$ BEGIN
    IF to_regclass('app.device_tokens') IS NOT NULL THEN
        INSERT INTO app.device_tokens (id, user_id, token) VALUES
        ('17060000-0000-4000-8000-000000000001','17000000-0000-4000-8000-000000000001','qa-device-a1'),
        ('17060000-0000-4000-8000-000000000002','17000000-0000-4000-8000-000000000001','qa-device-a2'),
        ('17060000-0000-4000-8000-000000000003','17000000-0000-4000-8000-000000000002','qa-device-b1')
        ON CONFLICT DO NOTHING;
    END IF;
END $$;

-- La evidencia de borrado no se elimina en cascada fuera de QA.
DO $$ BEGIN
    IF to_regclass('auth."PrivacyDispositionEvidence"') IS NOT NULL THEN
        DELETE FROM auth."PrivacyDispositionEvidence" e USING auth."PrivacyRequests" r
        WHERE e."RequestId" = r."Id"
          AND r."UserId" IN ('17000000-0000-4000-8000-000000000001',
                             '17000000-0000-4000-8000-000000000002',
                             '17000000-0000-4000-8000-000000000003');
    END IF;
    IF to_regclass('auth."PrivacyDeletionCases"') IS NOT NULL THEN
        DELETE FROM auth."PrivacyDeletionCases" c USING auth."PrivacyRequests" r
        WHERE c."RequestId" = r."Id"
          AND r."UserId" IN ('17000000-0000-4000-8000-000000000001',
                             '17000000-0000-4000-8000-000000000002',
                             '17000000-0000-4000-8000-000000000003');
    END IF;
END $$;

DELETE FROM auth."PrivacyRequests"
WHERE "UserId" IN ('17000000-0000-4000-8000-000000000001', '17000000-0000-4000-8000-000000000002',
                   '17000000-0000-4000-8000-000000000003');
COMMIT;
