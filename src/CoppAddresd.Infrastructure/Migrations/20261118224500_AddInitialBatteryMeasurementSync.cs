using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoppAddresd.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261118224500_AddInitialBatteryMeasurementSync")]
public sealed class AddInitialBatteryMeasurementSync : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // El default constante habilita el histórico y las filas nuevas sin reescribir la tabla.
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '3s';
            ALTER TABLE app.health_test_battery_assignments ADD COLUMN IF NOT EXISTS
                measurements_sync_due_at timestamptz DEFAULT TIMESTAMPTZ '1970-01-01 00:00:00+00';
            """);
        // Un intento interrumpido de CREATE INDEX CONCURRENTLY puede dejar un índice inválido.
        migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS app.ix_health_test_battery_measurement_sync_due;",
            suppressTransaction: true);
        migrationBuilder.Sql("""
            CREATE INDEX CONCURRENTLY ix_health_test_battery_measurement_sync_due
                ON app.health_test_battery_assignments (battery_id, measurements_sync_due_at, id)
                WHERE status = 'completed' AND measurements_sync_due_at IS NOT NULL;
            """, suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        SET LOCAL lock_timeout = '3s';
        ALTER TABLE app.health_test_battery_assignments DROP COLUMN measurements_sync_due_at;
        """);
}
