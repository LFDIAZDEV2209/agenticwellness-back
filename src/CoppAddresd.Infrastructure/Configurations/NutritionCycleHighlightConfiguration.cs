using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

public sealed class NutritionCycleHighlightConfiguration : IEntityTypeConfiguration<NutritionCycleHighlight>
{
    public void Configure(EntityTypeBuilder<NutritionCycleHighlight> b)
    {
        b.ToTable("nutrition_cycle_highlights", "app");
        b.HasKey(x => new { x.CycleId, x.IntakeLogId });
        b.Property(x => x.CycleId).HasColumnName("cycle_id");
        b.Property(x => x.IntakeLogId).HasColumnName("intake_log_id");
        b.Property(x => x.PinnedBy).HasColumnName("pinned_by");
        b.Property(x => x.PinnedAt).HasColumnName("pinned_at");
        b.Property(x => x.Note).HasColumnName("note").HasMaxLength(2000);
        b.HasOne<NutritionCycle>().WithMany().HasForeignKey(x => x.CycleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<NutritionIntakeLog>().WithMany().HasForeignKey(x => x.IntakeLogId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.IntakeLogId).HasDatabaseName("ix_cycle_highlights_intake");
    }
}
