using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

public sealed class VitaWriteReceiptConfiguration : IEntityTypeConfiguration<VitaWriteReceipt>
{
    public void Configure(EntityTypeBuilder<VitaWriteReceipt> builder)
    {
        builder.ToTable("vita_write_receipts", "app", table =>
            table.HasCheckConstraint("ck_vita_write_receipts_channel",
                "channel IN ('vita_chat', 'vita_voice', 'vita_whatsapp')"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.PatientId).HasColumnName("patient_id");
        builder.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(24);
        builder.Property(x => x.TurnId).HasColumnName("turn_id").HasMaxLength(256);
        builder.Property(x => x.ToolName).HasColumnName("tool_name").HasMaxLength(128);
        builder.Property(x => x.RequestHash).HasColumnName("request_hash").HasMaxLength(128);
        builder.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(64);
        builder.Property(x => x.EntityId).HasColumnName("entity_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        builder.HasIndex(x => new { x.PatientId, x.Channel, x.TurnId, x.ToolName })
            .HasDatabaseName("ux_vita_write_receipts_patient_channel_turn_tool").IsUnique();
        builder.HasOne<PatientProfile>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
    }
}
