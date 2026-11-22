namespace CoppAddresd.Domain.Entities;

public sealed class FamilyTeam
{
    public Guid Id { get; set; }
    public Guid LeaderPatientId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class FamilyTeamMembership
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid PatientId { get; set; }
}

public sealed class FamilyTeamInvitation
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class FamilyCamera
{
    public const string DemoProvider = "demo";

    public Guid Id { get; set; }
    public Guid OwnerPatientId { get; set; }
    public string Name { get; set; } = "";
    public string Provider { get; set; } = DemoProvider;
    public Guid? SharedMembershipId { get; set; }
    public bool SharedWithAdministrators { get; set; }
}
