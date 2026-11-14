using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations.ProgramProgress;

public sealed class NutritionIntakeRequestConfiguration : IEntityTypeConfiguration<NutritionIntakeRequest>
{
    public void Configure(EntityTypeBuilder<NutritionIntakeRequest> b)
    {
        b.ToTable("nutrition_intake_requests", "app");
        b.HasKey(x => new { x.PatientId, x.ClientRequestId });
        b.Property(x => x.PatientId).HasColumnName("patient_id");
        b.Property(x => x.ClientRequestId).HasColumnName("client_request_id").HasMaxLength(128);
        b.Property(x => x.RequestHash).HasColumnName("request_hash").HasMaxLength(64);
        b.Property(x => x.HabitCheckId).HasColumnName("habit_check_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasOne<PatientProfile>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<HabitCheck>().WithMany().HasForeignKey(x => x.HabitCheckId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.HabitCheckId).HasDatabaseName("ix_intake_requests_habit");
    }
}
