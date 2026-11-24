using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Auth.Migrations
{
    /// <inheritdoc />
    public partial class EnforceUniqueNormalizedEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM auth."Users"
                        WHERE "NormalizedEmail" IS NOT NULL
                        GROUP BY "NormalizedEmail"
                        HAVING count(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'KAN-122: reconcile duplicate normalized emails before enabling the unique index';
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS "UX_Users_NormalizedEmail"
                ON auth."Users" ("NormalizedEmail")
                WHERE "NormalizedEmail" IS NOT NULL;
                """,
                suppressTransaction: true);
            RequireValidIndex(migrationBuilder, "UX_Users_NormalizedEmail", unique: true);
            migrationBuilder.Sql(
                """DROP INDEX CONCURRENTLY IF EXISTS auth."EmailIndex";""",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "EmailIndex"
                ON auth."Users" ("NormalizedEmail");
                """,
                suppressTransaction: true);
            RequireValidIndex(migrationBuilder, "EmailIndex", unique: false);
            migrationBuilder.Sql(
                """DROP INDEX CONCURRENTLY IF EXISTS auth."UX_Users_NormalizedEmail";""",
                suppressTransaction: true);
        }

        private static void RequireValidIndex(MigrationBuilder migrationBuilder, string name, bool unique)
        {
            // Una creación concurrente fallida puede dejar un índice inválido.
            // Verificar el reemplazo antes de retirar el índice que ya funciona.
            migrationBuilder.Sql(
                $$"""
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1
                        FROM pg_catalog.pg_index
                        WHERE indexrelid = to_regclass('auth."{{name}}"')
                          AND indrelid = 'auth."Users"'::regclass
                          AND indisvalid
                          AND indisunique = {{(unique ? "TRUE" : "FALSE")}}
                    ) THEN
                        RAISE EXCEPTION 'Index auth."{{name}}" is invalid; inspect and drop the invalid index concurrently before retrying';
                    END IF;
                END
                $$;
                """);
        }
    }
}
