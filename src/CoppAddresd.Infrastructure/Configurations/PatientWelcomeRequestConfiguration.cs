using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

public sealed class PatientWelcomeRequestConfiguration : IEntityTypeConfiguration<PatientWelcomeRequest>
{
    public void Configure(EntityTypeBuilder<PatientWelcomeRequest> builder)
    {
        builder.ToTable("patient_welcome_requests", "erp");
        builder.HasKey(x => x.PatientId);
        builder.Property(x => x.PatientId).HasColumnName("patient_id");
        builder.Property(x => x.RequestedAt).HasColumnName("requested_at");
        builder.Property(x => x.TransferredAt).HasColumnName("transferred_at");
        builder.HasOne<PatientProfile>().WithOne().HasForeignKey<PatientWelcomeRequest>(x => x.PatientId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => new { x.RequestedAt, x.PatientId }).HasFilter("transferred_at IS NULL");
    }
}
