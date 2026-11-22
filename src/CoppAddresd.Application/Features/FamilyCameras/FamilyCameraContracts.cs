namespace CoppAddresd.Application.Features.FamilyCameras;

public sealed record FamilyTeamMemberDto(Guid Id, string Name, bool IsLeader);
public sealed record FamilyTeamDto(Guid Id, string Name, bool IsLeader, IReadOnlyList<FamilyTeamMemberDto> Members);
public sealed record FamilyCameraDto(Guid Id, string Name, string Provider, string OwnerName);
public sealed record OwnFamilyCameraDto(Guid Id, string Name, string Provider, bool SharedWithLeader, bool SharedWithAdministrators);
public sealed record FamilyCameraWorkspace(FamilyTeamDto? Team, IReadOnlyList<OwnFamilyCameraDto> OwnCameras, IReadOnlyList<FamilyCameraDto> SharedCameras);
public sealed record FamilyTeamInvitationDto(string Code, DateTimeOffset ExpiresAt);
public sealed record FamilyCameraFrame(byte[] Image);

public enum FamilyCameraChange
{
    Applied,
    NotFound,
    Conflict,
}

public interface IFamilyCameraStore
{
    Task<Guid?> FindActivePatientIdAsync(Guid userId, CancellationToken ct);
    Task<FamilyCameraWorkspace> GetWorkspaceAsync(Guid patientId, CancellationToken ct);
    Task<FamilyCameraChange> CreateTeamAsync(Guid patientId, string name, CancellationToken ct);
    Task<FamilyTeamInvitationDto?> InviteAsync(Guid leaderPatientId, CancellationToken ct);
    Task<FamilyCameraChange> JoinAsync(Guid patientId, string code, CancellationToken ct);
    Task<FamilyCameraChange> LeaveAsync(Guid patientId, CancellationToken ct);
    Task<OwnFamilyCameraDto> AddDemoAsync(Guid patientId, string name, CancellationToken ct);
    Task<FamilyCameraChange> SetSharingAsync(Guid patientId, Guid cameraId, bool? leader, bool? administrators, CancellationToken ct);
    Task<bool> RemoveCameraAsync(Guid patientId, Guid cameraId, CancellationToken ct);
    Task<string?> FindAppFeedAsync(Guid patientId, Guid cameraId, CancellationToken ct);
    Task<IReadOnlyList<FamilyCameraDto>?> ListAdminCamerasAsync(Guid patientId, Guid? clinicId, Guid? organizationId, CancellationToken ct);
    Task<string?> FindAdminFeedAsync(Guid patientId, Guid cameraId, Guid? clinicId, Guid? organizationId, CancellationToken ct);
}

/// <summary>Fuente de imágenes registrada en el servidor. No acepta direcciones de red del cliente.</summary>
public interface IFamilyCameraFeed
{
    string Provider { get; }
    ValueTask<FamilyCameraFrame?> ReadAsync(Guid cameraId, CancellationToken ct);
}
