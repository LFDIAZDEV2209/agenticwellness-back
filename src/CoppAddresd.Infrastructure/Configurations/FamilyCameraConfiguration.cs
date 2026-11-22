using CoppAddresd.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoppAddresd.Infrastructure.Configurations;

internal sealed class FamilyCameraConfiguration :
    IEntityTypeConfiguration<FamilyTeam>,
    IEntityTypeConfiguration<FamilyTeamMembership>,
    IEntityTypeConfiguration<FamilyTeamInvitation>,
    IEntityTypeConfiguration<FamilyCamera>
{
    public void Configure(EntityTypeBuilder<FamilyTeam> builder)
    {
        builder.ToTable("family_teams", "app");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.LeaderPatientId).HasColumnName("leader_patient_id");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(80).IsRequired();
        builder.HasOne<PatientProfile>().WithMany().HasForeignKey(x => x.LeaderPatientId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<FamilyTeamMembership> builder)
    {
        builder.ToTable("family_team_memberships", "app");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.TeamId).HasColumnName("team_id");
        builder.Property(x => x.PatientId).HasColumnName("patient_id");
        builder.HasOne<FamilyTeam>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PatientProfile>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.PatientId).IsUnique();
    }

    public void Configure(EntityTypeBuilder<FamilyTeamInvitation> builder)
    {
        builder.ToTable("family_team_invitations", "app");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.TeamId).HasColumnName("team_id");
        builder.Property(x => x.CodeHash).HasColumnName("code_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        builder.Property(x => x.UsedAt).HasColumnName("used_at");
        builder.HasOne<FamilyTeam>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.CodeHash).IsUnique();
    }

    public void Configure(EntityTypeBuilder<FamilyCamera> builder)
    {
        builder.ToTable("family_cameras", "app");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.OwnerPatientId).HasColumnName("owner_patient_id");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(80).IsRequired();
        builder.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(24).IsRequired();
        builder.Property(x => x.SharedMembershipId).HasColumnName("shared_membership_id");
        builder.Property(x => x.SharedWithAdministrators).HasColumnName("shared_with_administrators");
        builder.HasOne<PatientProfile>().WithMany().HasForeignKey(x => x.OwnerPatientId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<FamilyTeamMembership>().WithMany().HasForeignKey(x => x.SharedMembershipId).OnDelete(DeleteBehavior.SetNull);
    }
}
