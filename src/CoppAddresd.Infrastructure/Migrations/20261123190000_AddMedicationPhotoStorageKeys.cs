using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CoppAddresd.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261123190000_AddMedicationPhotoStorageKeys")]
public sealed class AddMedicationPhotoStorageKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "photo_storage_key", schema: "app", table: "medication_treatments",
            type: "character varying(250)", maxLength: 250, nullable: true);
        migrationBuilder.AddColumn<string>(name: "photo_storage_key", schema: "app", table: "medication_intake_events",
            type: "character varying(250)", maxLength: 250, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "photo_storage_key", schema: "app", table: "medication_intake_events");
        migrationBuilder.DropColumn(name: "photo_storage_key", schema: "app", table: "medication_treatments");
    }
}
