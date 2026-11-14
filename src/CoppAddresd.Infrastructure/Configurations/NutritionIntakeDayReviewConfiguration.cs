using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

public sealed class NutritionIntakeDayReviewConfiguration : IEntityTypeConfiguration<NutritionIntakeDayReview>
{
    public void Configure(EntityTypeBuilder<NutritionIntakeDayReview> b)
    {
        b.ToTable("nutrition_intake_day_reviews", "app");
        b.HasKey(x => new { x.PatientId, x.LocalDate });
        b.Property(x => x.PatientId).HasColumnName("patient_id");
        b.Property(x => x.LocalDate).HasColumnName("local_date");
        b.Property(x => x.CycleId).HasColumnName("cycle_id");
        b.Property(x => x.ReviewedBy).HasColumnName("reviewed_by");
        b.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
        b.HasOne<PatientProfile>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<NutritionCycle>().WithMany().HasForeignKey(x => x.CycleId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.CycleId).HasDatabaseName("ix_intake_day_reviews_cycle");
    }
}
