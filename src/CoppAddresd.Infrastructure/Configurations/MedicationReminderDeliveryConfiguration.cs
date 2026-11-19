using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

public sealed class MedicationReminderDeliveryConfiguration : IEntityTypeConfiguration<MedicationReminderDelivery>
{
    public void Configure(EntityTypeBuilder<MedicationReminderDelivery> builder)
    {
        builder.ToTable("medication_reminder_deliveries", "app");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).HasColumnName("id");
        builder.Property(row => row.PatientId).HasColumnName("patient_id");
        builder.Property(row => row.DoseId).HasColumnName("dose_id");
        builder.Property(row => row.Channel).HasColumnName("channel").HasMaxLength(24);
        builder.Property(row => row.Phase).HasColumnName("phase");
        builder.Property(row => row.ScheduledAt).HasColumnName("scheduled_at");
        builder.Property(row => row.Snoozed).HasColumnName("snoozed").HasDefaultValue(false);
        builder.Property(row => row.DueAt).HasColumnName("due_at");
        builder.Property(row => row.ClaimedAt).HasColumnName("claimed_at");
        builder.HasIndex(row => new { row.PatientId, row.DoseId, row.Channel, row.Phase }).IsUnique();
        builder.HasOne<PatientProfile>().WithMany().HasForeignKey(row => row.PatientId).OnDelete(DeleteBehavior.Cascade);
    }
}
