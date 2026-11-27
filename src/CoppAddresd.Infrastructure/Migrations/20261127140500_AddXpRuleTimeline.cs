using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations;

/// <summary>Historial inmutable del catálogo XP. No reescribe ningún asiento.</summary>
[DbContext(typeof(AppDbContext))]
[Migration("20261127140500_AddXpRuleTimeline")]
public sealed class AddXpRuleTimeline : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE app.xp_rule_revisions (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            code varchar(60) NOT NULL REFERENCES app.xp_rules (code) ON DELETE RESTRICT,
            superseded_at timestamptz NOT NULL,
            snapshot jsonb NOT NULL
        );
        CREATE INDEX ix_xp_rule_revisions_lookup ON app.xp_rule_revisions (code, superseded_at, id);
        GRANT SELECT, INSERT ON app.xp_rule_revisions TO app_user;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE app.xp_rule_revisions;");
}
