using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

public sealed class RxTermsProductConfiguration : IEntityTypeConfiguration<RxTermsProduct>
{
    public void Configure(EntityTypeBuilder<RxTermsProduct> b)
    {
        b.ToTable("rxterms_products", "app");
        b.HasKey(x => x.RxCui);
        b.Property(x => x.RxCui).HasColumnName("rx_cui").HasMaxLength(18);
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.SearchText).HasColumnName("search_text");
        b.Property(x => x.Strength).HasColumnName("strength");
        b.Property(x => x.DoseForm).HasColumnName("dose_form");
        b.Property(x => x.Route).HasColumnName("route");
        b.Property(x => x.DoseUnit).HasColumnName("dose_unit");
        b.Property(x => x.Release).HasColumnName("release").HasMaxLength(6);
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.HasIndex(x => x.SearchText).HasDatabaseName("ix_rxterms_search_trgm")
            .HasMethod("gin").HasOperators("gin_trgm_ops").HasFilter("is_active");
    }
}
