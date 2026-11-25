using CoppAddresd.Domain.Entities;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

internal sealed class WellnessJourneyGenerationConfiguration : IEntityTypeConfiguration<WellnessJourneyGeneration>
{
    public void Configure(EntityTypeBuilder<WellnessJourneyGeneration> builder)
    {
        builder.ToTable("wellness_journey_generations", "app", table =>
        {
            table.HasCheckConstraint("ck_wellness_journey_attempts", "attempts > 0");
            table.HasCheckConstraint("ck_wellness_journey_status", "status IN ('pending', 'running', 'ready', 'failed')");
            table.HasCheckConstraint("ck_wellness_journey_lease",
                "(status = 'running' AND lease_id IS NOT NULL AND lease_until IS NOT NULL) OR (status <> 'running' AND lease_id IS NULL AND lease_until IS NULL)");
            table.HasCheckConstraint("ck_wellness_journey_result", "(status = 'ready') = (result_json IS NOT NULL)");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.SubmissionId).HasColumnName("submission_id");
        builder.Property(x => x.PatientId).HasColumnName("patient_id");
        builder.Property(x => x.PaymentId).HasColumnName("payment_id");
        builder.Property(x => x.DraftHash).HasColumnName("draft_hash").HasMaxLength(64);
        builder.Property(x => x.DraftJson).HasColumnName("draft_json").HasColumnType("jsonb");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(12);
        builder.Property(x => x.Attempts).HasColumnName("attempts");
        builder.Property(x => x.LeaseId).HasColumnName("lease_id");
        builder.Property(x => x.LeaseUntil).HasColumnName("lease_until");
        builder.Property(x => x.ResultJson).HasColumnName("result_json").HasColumnType("jsonb");
        builder.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(48);

        builder.HasOne<PatientProfile>().WithMany().HasForeignKey(x => x.PatientId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.PatientId).HasDatabaseName("ix_wellness_journey_patient_id");
        builder.HasIndex(x => x.PaymentId).IsUnique().HasDatabaseName("ux_wellness_journey_payment_id");
        builder.HasIndex(x => new { x.CreatedAt, x.Id }).HasDatabaseName("ix_wellness_journey_claimable")
            .HasFilter("status IN ('pending', 'running')");
    }
}
