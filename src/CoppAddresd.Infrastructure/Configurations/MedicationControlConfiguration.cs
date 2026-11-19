using System.Text.RegularExpressions;
using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

public sealed class MedicationControlConfiguration :
    IEntityTypeConfiguration<MedicationTreatment>, IEntityTypeConfiguration<MedicationRegimen>,
    IEntityTypeConfiguration<MedicationScheduleSlot>, IEntityTypeConfiguration<MedicationDose>,
    IEntityTypeConfiguration<MedicationIntakeEvent>, IEntityTypeConfiguration<PatientModuleAccess>,
    IEntityTypeConfiguration<MedicationTreatmentStatusEvent>, IEntityTypeConfiguration<MedicationDoseOperation>,
    IEntityTypeConfiguration<MedicationPatientState>
{
    private static void Common<T>(EntityTypeBuilder<T> b, string table) where T : class
    {
        b.ToTable(table, "app");
        b.HasKey("Id");
        foreach (var property in b.Metadata.GetProperties())
        {
            property.SetColumnName(Regex.Replace(property.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant());
            if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                property.SetColumnType("timestamptz");
        }
    }

    public void Configure(EntityTypeBuilder<MedicationTreatment> b)
    {
        Common(b, "medication_treatments");
        b.Property(x => x.Name).HasColumnType("text");
        b.Property(x => x.RxCui).HasMaxLength(18);
        b.HasOne<RxTermsProduct>().WithMany().HasForeignKey(x => x.RxCui).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.ProductType).HasMaxLength(20);
        b.Property(x => x.Origin).HasMaxLength(30);
        b.Property(x => x.Status).HasMaxLength(20);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasOne<PatientProfile>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Medication>().WithMany().HasForeignKey(x => x.MedicationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.ClientRequestId }).IsUnique().HasDatabaseName("ux_med_treatment_operation");
        b.HasIndex(x => new { x.PatientId, x.Status }).HasDatabaseName("ix_med_treatment_patient_status");
        b.HasIndex(x => x.MedicationId);
        b.ToTable("medication_treatments", "app", t =>
        {
            t.HasCheckConstraint("ck_med_treatment_type", "product_type IN ('medication','vitamin','supplement')");
            t.HasCheckConstraint("ck_med_treatment_status", "status IN ('active','paused','completed')");
            t.HasCheckConstraint("ck_med_treatment_revision", "revision > 0");
        });
    }

    public void Configure(EntityTypeBuilder<MedicationRegimen> b)
    {
        Common(b, "medication_regimens");
        b.Property(x => x.Dosage).HasPrecision(18, 4);
        b.Property(x => x.Unit).HasMaxLength(40);
        b.Property(x => x.Route).HasMaxLength(80);
        b.Property(x => x.Instructions).HasMaxLength(2000);
        b.Property(x => x.TimeZone).HasMaxLength(100);
        b.HasOne<MedicationTreatment>().WithMany().HasForeignKey(x => x.TreatmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TreatmentId, x.Version }).IsUnique().HasDatabaseName("ux_med_regimen_version");
        b.ToTable("medication_regimens", "app", t =>
        {
            t.HasCheckConstraint("ck_med_regimen_quantity", "dosage > 0");
            t.HasCheckConstraint("ck_med_regimen_recurrence", "(weekday_mask IS NULL OR weekday_mask BETWEEN 1 AND 127) AND (interval_hours IS NULL OR interval_hours BETWEEN 3 AND 168) AND (weekday_mask IS NULL OR interval_hours IS NULL)");
            t.HasCheckConstraint("ck_med_regimen_dates", "end_date IS NULL OR end_date >= start_date");
        });
    }

    public void Configure(EntityTypeBuilder<MedicationScheduleSlot> b)
    {
        Common(b, "medication_schedule_slots");
        b.HasOne<MedicationRegimen>().WithMany().HasForeignKey(x => x.RegimenId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.RegimenId, x.LocalTime }).IsUnique().HasDatabaseName("ux_med_schedule_time");
    }

    public void Configure(EntityTypeBuilder<MedicationDose> b)
    {
        Common(b, "medication_doses");
        b.Property(x => x.TimeZone).HasMaxLength(100);
        b.Property(x => x.Status).HasMaxLength(20);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasOne<PatientProfile>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MedicationTreatment>().WithMany().HasForeignKey(x => x.TreatmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MedicationRegimen>().WithMany().HasForeignKey(x => x.RegimenId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MedicationScheduleSlot>().WithMany().HasForeignKey(x => x.SlotId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.SlotId, x.ScheduledAt }).IsUnique().HasDatabaseName("ux_med_dose_slot_at");
        b.HasIndex(x => new { x.PatientId, x.ScheduledAt }).HasDatabaseName("ix_med_dose_patient_at");
        b.HasIndex(x => x.TreatmentId);
        b.HasIndex(x => x.RegimenId);
        b.ToTable("medication_doses", "app", t =>
            t.HasCheckConstraint("ck_med_dose_status", "status IN ('pending','taken','omitted','cancelled')"));
    }

    public void Configure(EntityTypeBuilder<MedicationIntakeEvent> b)
    {
        Common(b, "medication_intake_events");
        b.Property(x => x.Status).HasMaxLength(20);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.Channel).HasMaxLength(20);
        b.Property(x => x.Origin).HasMaxLength(30);
        b.HasOne<PatientProfile>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MedicationTreatment>().WithMany().HasForeignKey(x => x.TreatmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MedicationDose>().WithMany().HasForeignKey(x => x.DoseId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MedicationIntakeEvent>().WithMany().HasForeignKey(x => x.CorrectsEventId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.ClientRequestId }).IsUnique().HasDatabaseName("ux_med_event_operation");
        b.HasIndex(x => new { x.PatientId, x.RecordedAt }).HasDatabaseName("ix_med_event_patient_at");
        b.HasIndex(x => x.TreatmentId);
        b.HasIndex(x => x.DoseId);
        b.HasIndex(x => x.CorrectsEventId);
        b.ToTable("medication_intake_events", "app", t =>
            t.HasCheckConstraint("ck_med_event_status", "status IN ('taken','omitted','pending')"));
    }

    public void Configure(EntityTypeBuilder<PatientModuleAccess> b)
    {
        Common(b, "patient_module_access");
        b.Property(x => x.ModuleCode).HasMaxLength(50);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasOne<PatientProfile>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.ModuleCode }).IsUnique().HasDatabaseName("ux_patient_module_access");
    }
    public void Configure(EntityTypeBuilder<MedicationTreatmentStatusEvent> b)
    {
        Common(b, "medication_treatment_status_events");
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.Status).HasMaxLength(20);
        b.HasOne<MedicationTreatment>().WithMany().HasForeignKey(x => x.TreatmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TreatmentId, x.Revision }).IsUnique().HasDatabaseName("ux_med_status_revision");
        b.ToTable("medication_treatment_status_events", "app", t =>
        {
            t.HasCheckConstraint("ck_med_status_value", "status IN ('active','paused','completed')");
            t.HasCheckConstraint("ck_med_status_revision", "revision > 0");
        });
    }

    public void Configure(EntityTypeBuilder<MedicationDoseOperation> b)
    {
        Common(b, "medication_dose_operations");
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.HasOne<PatientProfile>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MedicationDose>().WithMany().HasForeignKey(x => x.DoseId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.ClientRequestId }).IsUnique().HasDatabaseName("ux_med_dose_operation");
        b.HasIndex(x => x.DoseId);
    }

    public void Configure(EntityTypeBuilder<MedicationPatientState> b)
    {
        Common(b, "medication_patient_state");
        b.HasOne<PatientProfile>().WithOne().HasForeignKey<MedicationPatientState>(x => x.Id).OnDelete(DeleteBehavior.Restrict);
    }
}
