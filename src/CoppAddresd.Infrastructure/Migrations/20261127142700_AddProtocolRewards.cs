using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoppAddresd.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261127142700_AddProtocolRewards")]
public sealed class AddProtocolRewards : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE app.protocol_reward_entries (
            patient_id uuid NOT NULL REFERENCES app.patient_profiles (id) ON DELETE CASCADE,
            local_date date NOT NULL,
            step varchar(12) NOT NULL CHECK (step IN ('exercise', 'diet', 'vitals', 'emotional', 'chest', 'close')),
            amount integer NOT NULL CHECK (amount >= 0),
            awarded_at timestamptz NOT NULL,
            plan_day integer NULL CHECK (plan_day BETWEEN 1 AND 365),
            PRIMARY KEY (patient_id, local_date, step)
        );
        CREATE INDEX ix_protocol_rewards_plan_day ON app.protocol_reward_entries (patient_id, plan_day, local_date)
            WHERE plan_day IS NOT NULL;
        GRANT SELECT, INSERT ON app.protocol_reward_entries TO app_user;

        INSERT INTO app.xp_rules (code, name, category, base_xp, multiplier, active, valid_from)
        VALUES
            ('PROTOCOL_EXERCISE', 'Protocolo · Ejercicio', 'adherence', 10, 1, true, '2026-01-01'),
            ('PROTOCOL_DIET', 'Protocolo · Dieta', 'adherence', 10, 1, true, '2026-01-01'),
            ('PROTOCOL_VITALS', 'Protocolo · Vitales', 'adherence', 10, 1, true, '2026-01-01'),
            ('PROTOCOL_EMOTIONAL', 'Protocolo · Emocional', 'adherence', 10, 1, true, '2026-01-01'),
            ('PROTOCOL_CHEST', 'Protocolo · Cofre', 'adherence', 10, 1, true, '2026-01-01'),
            ('PROTOCOL_SEQUENCE', 'Protocolo · Bonus de secuencia', 'adherence', 20, 1, true, '2026-01-01');
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE app.protocol_reward_entries;
        DELETE FROM app.xp_rule_revisions WHERE code IN ('PROTOCOL_EXERCISE','PROTOCOL_DIET',
            'PROTOCOL_VITALS','PROTOCOL_EMOTIONAL','PROTOCOL_CHEST','PROTOCOL_SEQUENCE');
        DELETE FROM app.xp_rules WHERE code IN ('PROTOCOL_EXERCISE','PROTOCOL_DIET',
            'PROTOCOL_VITALS','PROTOCOL_EMOTIONAL','PROTOCOL_CHEST','PROTOCOL_SEQUENCE');
        """);
}
