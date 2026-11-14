using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

public sealed class NutritionCycleConfiguration : IEntityTypeConfiguration<NutritionCycle>
{
    public void Configure(EntityTypeBuilder<NutritionCycle> b)
    {
        b.ToTable("nutrition_cycles", "app", t =>
        {
            t.HasCheckConstraint("ck_nutrition_cycle_state", "state IN ('borrador','en_curso','por_revisar','revisado','interrumpido')");
            t.HasCheckConstraint("ck_nutrition_cycle_publication", "(state = 'borrador' AND number IS NULL AND start_date IS NULL AND end_date IS NULL) OR (state <> 'borrador' AND number > 0 AND number IS NOT NULL AND start_date IS NOT NULL AND end_date IS NOT NULL AND end_date >= start_date AND end_date - start_date < 30)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.PatientId).HasColumnName("patient_id");
        b.Property(x => x.Number).HasColumnName("number");
        b.Property(x => x.StartDate).HasColumnName("start_date");
        b.Property(x => x.EndDate).HasColumnName("end_date");
        b.Property(x => x.State).HasColumnName("state").HasMaxLength(30);
        b.HasOne<PatientProfile>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.Number }).IsUnique().HasDatabaseName("uq_nutrition_cycles_patient_number");
    }
}
