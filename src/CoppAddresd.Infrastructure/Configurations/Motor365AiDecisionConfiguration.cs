using CoppAddresd.Domain.Wellness365;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

internal sealed class Motor365AiDecisionConfiguration : IEntityTypeConfiguration<Motor365AiDecision>
{
    public void Configure(EntityTypeBuilder<Motor365AiDecision> builder)
    {
        builder.ToTable("ai_plan_decision", "app", table =>
        {
            table.HasCheckConstraint("ck_motor_ai_day", "day BETWEEN 1 AND 365");
            table.HasCheckConstraint("ck_motor_ai_status", """
                (status = 'applied' AND proposed IS NOT NULL AND code IS NULL AND reverts IS NULL) OR
                (status IN ('rejected', 'unavailable') AND code IS NOT NULL AND reverts IS NULL) OR
                (status = 'reverted' AND reverts IS NOT NULL AND proposed IS NULL AND code IS NULL)
                """);
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(x => x.Sequence).HasColumnName("sequence").UseIdentityAlwaysColumn();
        builder.Property(x => x.PersonId).HasColumnName("person_id");
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(x => x.Day).HasColumnName("day");
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at");
        builder.Property(x => x.EngineVersion).HasColumnName("engine_ver").HasMaxLength(24);
        builder.Property(x => x.ParametersVersion).HasColumnName("params_version");
        builder.Property(x => x.BaseFingerprint).HasColumnName("base_fingerprint").HasMaxLength(64);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(16);
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(x => x.ReasonEs).HasColumnName("reason_es").HasMaxLength(240);
        builder.Property(x => x.ReasonEn).HasColumnName("reason_en").HasMaxLength(240);
        builder.Property(x => x.ProposedJson).HasColumnName("proposed").HasColumnType("jsonb");
        builder.Property(x => x.Reverts).HasColumnName("reverts");
        builder.HasOne<Motor365Person>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Motor365AiDecision>().WithMany().HasForeignKey(x => x.Reverts).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.Reverts).IsUnique();
        builder.HasIndex(x => new { x.PersonId, x.Day, x.Sequence }).IsUnique();
    }
}
