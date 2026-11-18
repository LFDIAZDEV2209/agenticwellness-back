using System.Text.RegularExpressions;
using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

public sealed class MedicationMonitoringConfiguration : IEntityTypeConfiguration<MedicationSupply>,
    IEntityTypeConfiguration<MedicationStockMovement>, IEntityTypeConfiguration<MedicationEffectReport>,
    IEntityTypeConfiguration<MedicationEffectFollowUp>, IEntityTypeConfiguration<MedicationMonitoringAlert>,
    IEntityTypeConfiguration<MedicationAlertFollowUp>, IEntityTypeConfiguration<MedicationMonitoringOperation>,
    IEntityTypeConfiguration<MedicationAuditEntry>
{
    private static void Common<T>(EntityTypeBuilder<T> b, string table) where T : class
    {
        b.ToTable(table, "app"); b.HasKey("Id");
        foreach (var p in b.Metadata.GetProperties())
        {
            p.SetColumnName(Regex.Replace(p.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant());
            if (p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?)) p.SetColumnType("timestamptz");
            if (p.ClrType == typeof(decimal)) { p.SetPrecision(18); p.SetScale(4); }
        }
        b.HasOne<PatientProfile>().WithMany().HasForeignKey("PatientId").OnDelete(DeleteBehavior.Restrict);
    }
    public void Configure(EntityTypeBuilder<MedicationSupply> b)
    {
        Common(b, "medication_supplies"); b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasOne<MedicationTreatment>().WithMany().HasForeignKey(x => x.TreatmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TreatmentId).IsUnique(); b.HasIndex(x => new { x.PatientId, x.ExpiresOn });
        b.ToTable("medication_supplies", "app", t => t.HasCheckConstraint("ck_med_supply_revision", "revision > 0"));
    }
    public void Configure(EntityTypeBuilder<MedicationStockMovement> b)
    {
        Common(b, "medication_stock_movements"); b.Property(x => x.Kind).HasMaxLength(30);
        b.Property(x => x.Origin).HasMaxLength(30); b.Property(x => x.Reason).HasMaxLength(500);
        b.HasOne<MedicationSupply>().WithMany().HasForeignKey(x => x.SupplyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MedicationDose>().WithMany().HasForeignKey(x => x.DoseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MedicationIntakeEvent>().WithMany().HasForeignKey(x => x.IntakeEventId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.RecordedAt }); b.HasIndex(x => new { x.SupplyId, x.RecordedAt });
        b.HasIndex(x => x.DoseId); b.HasIndex(x => x.IntakeEventId).IsUnique();
        b.ToTable("medication_stock_movements", "app", t => t.HasCheckConstraint("ck_med_stock_kind", "kind IN ('initial','refill','adjustment','consumption','correction')"));
    }
    public void Configure(EntityTypeBuilder<MedicationEffectReport> b)
    {
        Common(b, "medication_effect_reports"); b.Property(x => x.Symptom).HasMaxLength(200);
        b.Property(x => x.Severity).HasMaxLength(20); b.Property(x => x.Status).HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(1000); b.Property(x => x.Origin).HasMaxLength(30);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasOne<MedicationTreatment>().WithMany().HasForeignKey(x => x.TreatmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.CreatedAt }); b.HasIndex(x => x.TreatmentId);
        b.ToTable("medication_effect_reports", "app", t => {
            t.HasCheckConstraint("ck_med_effect_severity", "severity IN ('mild','moderate','severe')");
            t.HasCheckConstraint("ck_med_effect_status", "status IN ('open','follow_up','resolved')"); });
    }
    public void Configure(EntityTypeBuilder<MedicationEffectFollowUp> b)
    {
        Common(b, "medication_effect_followups"); b.Property(x => x.Status).HasMaxLength(20); b.Property(x => x.Note).HasMaxLength(1000);
        b.HasOne<MedicationEffectReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ReportId, x.CreatedAt }); b.HasIndex(x => x.PatientId);
    }
    public void Configure(EntityTypeBuilder<MedicationMonitoringAlert> b)
    {
        Common(b, "medication_monitoring_alerts"); b.Property(x => x.DedupKey).HasMaxLength(160);
        b.Property(x => x.Kind).HasMaxLength(30); b.Property(x => x.Severity).HasMaxLength(20);
        b.Property(x => x.Status).HasMaxLength(20); b.Property(x => x.ResolutionNote).HasMaxLength(1000);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasOne<MedicationTreatment>().WithMany().HasForeignKey(x => x.TreatmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.DedupKey }).IsUnique(); b.HasIndex(x => new { x.Status, x.UpdatedAt }); b.HasIndex(x => x.TreatmentId);
        b.ToTable("medication_monitoring_alerts", "app", t => t.HasCheckConstraint("ck_med_alert_status", "status IN ('open','in_progress','resolved')"));
    }
    public void Configure(EntityTypeBuilder<MedicationAlertFollowUp> b)
    {
        Common(b, "medication_alert_followups"); b.Property(x => x.Status).HasMaxLength(20); b.Property(x => x.Note).HasMaxLength(1000);
        b.HasOne<MedicationMonitoringAlert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AlertId, x.CreatedAt }); b.HasIndex(x => x.PatientId);
    }
    public void Configure(EntityTypeBuilder<MedicationMonitoringOperation> b)
    {
        Common(b, "medication_monitoring_operations"); b.Property(x => x.Kind).HasMaxLength(30); b.Property(x => x.RequestHash).HasMaxLength(64);
        b.HasIndex(x => new { x.PatientId, x.ClientRequestId }).IsUnique();
    }
    public void Configure(EntityTypeBuilder<MedicationAuditEntry> b)
    {
        Common(b, "medication_audit_entries"); b.Property(x => x.Action).HasMaxLength(40); b.Property(x => x.Resource).HasMaxLength(40);
        b.Property(x => x.Origin).HasMaxLength(30); b.Property(x => x.Purpose).HasMaxLength(200);
        b.Property(x => x.ActorEmail).HasMaxLength(320); b.Property(x => x.RequestId).HasMaxLength(200);
        b.HasIndex(x => new { x.PatientId, x.CreatedAt }); b.HasIndex(x => new { x.Action, x.CreatedAt }); b.HasIndex(x => new { x.ActorId, x.CreatedAt });
    }
}
