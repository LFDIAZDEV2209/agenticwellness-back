using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

public sealed class NutritionCycleReviewConfiguration : IEntityTypeConfiguration<NutritionCycleReview>
{
    public void Configure(EntityTypeBuilder<NutritionCycleReview> b)
    {
        b.ToTable("nutrition_cycle_reviews", "app"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id"); b.Property(x => x.CycleId).HasColumnName("cycle_id");
        b.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken();
        b.Property(x => x.ReviewerId).HasColumnName("reviewer_id"); b.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
        b.Property(x => x.ProfessionalName).HasColumnName("professional_name").HasMaxLength(400);
        b.Property(x => x.SummaryJson).HasColumnName("summary_json").HasColumnType("jsonb");
        b.Property(x => x.AiDraftJson).HasColumnName("ai_draft_json").HasColumnType("jsonb");
        b.Property(x => x.AiDraftConfirmed).HasColumnName("ai_draft_confirmed");
        b.Property(x => x.ProfessionalNotes).HasColumnName("professional_notes");
        b.Property(x => x.PatientMessageJson).HasColumnName("patient_message").HasColumnType("jsonb");
        b.Property(x => x.CriticalAlertsConfirmed).HasColumnName("critical_alerts_confirmed");
        b.Property(x => x.PublishedAt).HasColumnName("published_at");
        b.Property(x => x.NextPrescriptionId).HasColumnName("next_prescription_id");
        b.HasIndex(x => x.CycleId).IsUnique().HasDatabaseName("uq_nutrition_cycle_reviews_cycle");
        b.HasOne<NutritionCycle>().WithMany().HasForeignKey(x => x.CycleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<NutritionPrescription>().WithMany().HasForeignKey(x => x.NextPrescriptionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class NutritionCycleAttachmentConfiguration : IEntityTypeConfiguration<NutritionCycleAttachment>
{
    public void Configure(EntityTypeBuilder<NutritionCycleAttachment> b)
    {
        b.ToTable("nutrition_cycle_attachments", "app", t => t.HasCheckConstraint("ck_nutrition_cycle_attachment_kind", "kind IN ('image','document')"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ReviewId).HasColumnName("review_id");
        b.Property(x => x.StorageKey).HasColumnName("storage_key").HasMaxLength(512);
        b.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(20);
        b.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(100);
        b.Property(x => x.Length).HasColumnName("length");
        b.Property(x => x.Caption).HasColumnName("caption").HasMaxLength(2000);
        b.Property(x => x.VisibleToPatient).HasColumnName("visible_to_patient");
        b.HasOne(x => x.Review).WithMany(x => x.Attachments).HasForeignKey(x => x.ReviewId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ReviewId).HasDatabaseName("ix_nutrition_cycle_attachments_review");
        b.HasIndex(x => x.StorageKey).IsUnique().HasDatabaseName("uq_nutrition_cycle_attachment_key");
    }
}
