using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoppAddresd.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261122210000_AddRxTermsProductMetadata")]
public sealed class AddRxTermsProductMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE app.rxterms_products
            ADD COLUMN strength text,
            ADD COLUMN dose_form text,
            ADD COLUMN route text,
            ADD COLUMN dose_unit text;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE app.rxterms_products
            DROP COLUMN strength,
            DROP COLUMN dose_form,
            DROP COLUMN route,
            DROP COLUMN dose_unit;
        """);
}
