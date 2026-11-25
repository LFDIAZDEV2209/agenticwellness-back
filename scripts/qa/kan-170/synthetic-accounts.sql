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

DELETE FROM auth."PrivacyRequests"
WHERE "UserId" IN ('17000000-0000-4000-8000-000000000001', '17000000-0000-4000-8000-000000000002',
                   '17000000-0000-4000-8000-000000000003');
COMMIT;
