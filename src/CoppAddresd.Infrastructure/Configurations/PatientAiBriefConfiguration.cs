using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

/// <summary>Persistencia mínima de resúmenes y sugerencias independientes de la asignación inicial.</summary>
public sealed class PatientAiBriefConfiguration : IEntityTypeConfiguration<PatientAiBrief>
{
    public void Configure(EntityTypeBuilder<PatientAiBrief> builder)
    {
        builder.ToTable("patient_ai_briefs", "app", table =>
        {
            table.HasComment("Borradores IA del paciente; sin publicación ni aplicación automática de cambios clínicos.");
            table.HasCheckConstraint("ck_patient_ai_briefs_kind", "kind IN ('patient_summary', 'weekly_plan_suggestions')");
            table.HasCheckConstraint("ck_patient_ai_briefs_state", "state IN ('draft', 'applied', 'dismissed')");
            table.HasCheckConstraint("ck_patient_ai_briefs_draft", "jsonb_typeof(draft_json) = 'object'");
            table.HasCheckConstraint("ck_patient_ai_briefs_input_hash", "input_hash ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("ck_patient_ai_briefs_model", "model_name IS NULL OR length(btrim(model_name)) > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.PatientId).HasColumnName("patient_id");
        builder.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(32);
        builder.Property(x => x.DraftJson).HasColumnName("draft_json").HasColumnType("jsonb");
        builder.Property(x => x.ModelName).HasColumnName("model_name").HasMaxLength(200);
        builder.Property(x => x.GeneratedAt).HasColumnName("generated_at").HasColumnType("timestamptz");
        builder.Property(x => x.InputHash).HasColumnName("input_hash").HasMaxLength(64);
        builder.Property(x => x.State).HasColumnName("state").HasMaxLength(16).HasDefaultValue("draft");
        builder.HasOne<PatientProfile>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        // El prefijo patient_id también cubre la FK; fecha e id permiten un orden estable del historial.
        builder.HasIndex(x => new { x.PatientId, x.Kind, x.GeneratedAt, x.Id })
            .IsDescending(false, false, true, true).HasDatabaseName("ix_patient_ai_briefs_patient_kind_generated");
    }
}
