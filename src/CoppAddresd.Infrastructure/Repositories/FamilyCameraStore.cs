using System.Security.Cryptography;
using System.Text;
using CoppAddresd.Application.Features.FamilyCameras;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoppAddresd.Infrastructure.Repositories;

internal sealed class FamilyCameraStore(AppDbContext db, TimeProvider clock) : IFamilyCameraStore
{
    private IQueryable<PatientProfile> ActivePatients => db.PatientProfiles.AsNoTracking()
        .Where(x => x.DeletedAt == null && x.Status == "Activo");
    private DbSet<FamilyTeam> Teams => db.Set<FamilyTeam>();
    private DbSet<FamilyTeamMembership> Memberships => db.Set<FamilyTeamMembership>();
    private DbSet<FamilyTeamInvitation> Invitations => db.Set<FamilyTeamInvitation>();
    private DbSet<FamilyCamera> Cameras => db.Set<FamilyCamera>();

    public Task<Guid?> FindActivePatientIdAsync(Guid userId, CancellationToken ct) =>
        ActivePatients.Where(x => x.UserId == userId)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);

    public async Task<FamilyCameraWorkspace> GetWorkspaceAsync(Guid patientId, CancellationToken ct)
    {
        var membership = await GetMembershipAsync(patientId, ct);
        FamilyTeamDto? team = null;
        if (membership is not null)
        {
            var members = await (
                from member in Memberships.AsNoTracking()
                join patient in ActivePatients on member.PatientId equals patient.Id
                where member.TeamId == membership.TeamId
                orderby member.PatientId == membership.LeaderId descending, patient.FirstName, patient.LastName
                select new FamilyTeamMemberDto(patient.Id, patient.FirstName + " " + patient.LastName,
                    patient.Id == membership.LeaderId)
            ).ToListAsync(ct);
            team = new FamilyTeamDto(membership.TeamId, membership.TeamName, patientId == membership.LeaderId, members);
        }

        var membershipId = membership?.Id;
        var own = await Cameras.AsNoTracking().Where(x => x.OwnerPatientId == patientId)
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new OwnFamilyCameraDto(x.Id, x.Name, x.Provider,
                membershipId != null && x.SharedMembershipId == membershipId, x.SharedWithAdministrators))
            .ToListAsync(ct);
        var shared = team is { IsLeader: true } ? await (
            from camera in SharedWithLeader(patientId)
            join owner in ActivePatients on camera.OwnerPatientId equals owner.Id
            orderby owner.FirstName, owner.LastName, camera.Name, camera.Id
            select new FamilyCameraDto(camera.Id, camera.Name, camera.Provider, owner.FirstName + " " + owner.LastName)
        ).ToListAsync(ct) : [];
        return new FamilyCameraWorkspace(team, own, shared);
    }

    public async Task<FamilyCameraChange> CreateTeamAsync(Guid patientId, string name, CancellationToken ct)
    {
        if (await Memberships.AnyAsync(x => x.PatientId == patientId, ct))
            return FamilyCameraChange.Conflict;

        var team = new FamilyTeam { Id = Guid.NewGuid(), LeaderPatientId = patientId, Name = name };
        Teams.Add(team);
        Memberships.Add(new FamilyTeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, PatientId = patientId });
        return await SaveChangeAsync(ct);
    }

    public async Task<FamilyTeamInvitationDto?> InviteAsync(Guid leaderPatientId, CancellationToken ct)
    {
        var membership = await GetMembershipAsync(leaderPatientId, ct);
        if (membership is null || membership.LeaderId != leaderPatientId)
            return null;

        var now = clock.GetUtcNow();
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        var invite = new FamilyTeamInvitation
        {
            Id = Guid.NewGuid(), TeamId = membership.TeamId, CodeHash = HashCode(code), ExpiresAt = now.AddHours(12)
        };
        await Invitations.Where(x => x.TeamId == invite.TeamId && x.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        Invitations.Add(invite);
        return await SaveChangeAsync(ct) == FamilyCameraChange.Applied
            ? new FamilyTeamInvitationDto(code, invite.ExpiresAt) : null;
    }

    public async Task<FamilyCameraChange> JoinAsync(Guid patientId, string code, CancellationToken ct)
    {
        if (await Memberships.AnyAsync(x => x.PatientId == patientId, ct))
            return FamilyCameraChange.Conflict;

        var now = clock.GetUtcNow();
        var digest = HashCode(code);
        var membershipId = Guid.NewGuid();
        try
        {
            // Consumir el código y crear la membresía en una única sentencia atómica.
            var added = await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH consumed AS (
                    UPDATE app.family_team_invitations AS invitation
                    SET used_at = {now}
                    WHERE code_hash = {digest} AND used_at IS NULL AND expires_at > {now}
                      AND EXISTS (
                        SELECT 1 FROM app.family_teams AS team
                        JOIN app.patient_profiles AS leader ON leader.id = team.leader_patient_id
                        WHERE team.id = invitation.team_id AND leader.deleted_at IS NULL AND leader.status = 'Activo'
                      )
                    RETURNING team_id
                )
                INSERT INTO app.family_team_memberships (id, team_id, patient_id)
                SELECT {membershipId}, team_id, {patientId} FROM consumed
                """, ct);
            return added == 1 ? FamilyCameraChange.Applied : FamilyCameraChange.NotFound;
        }
        catch (PostgresException ex) when (IsWriteConflict(ex))
        {
            return FamilyCameraChange.Conflict;
        }
    }

    public async Task<FamilyCameraChange> LeaveAsync(Guid patientId, CancellationToken ct)
    {
        var membership = await GetMembershipAsync(patientId, ct);
        if (membership is null)
            return FamilyCameraChange.NotFound;

        if (membership.LeaderId == patientId)
            await Teams.Where(x => x.Id == membership.TeamId && x.LeaderPatientId == patientId).ExecuteDeleteAsync(ct);
        else
            await Memberships.Where(x => x.Id == membership.Id).ExecuteDeleteAsync(ct);
        return FamilyCameraChange.Applied;
    }

    public async Task<OwnFamilyCameraDto> AddDemoAsync(Guid patientId, string name, CancellationToken ct)
    {
        var camera = new FamilyCamera { Id = Guid.NewGuid(), OwnerPatientId = patientId, Name = name };
        Cameras.Add(camera);
        await db.SaveChangesAsync(ct);
        return new OwnFamilyCameraDto(camera.Id, camera.Name, camera.Provider, false, false);
    }

    public async Task<FamilyCameraChange> SetSharingAsync(Guid patientId, Guid cameraId, bool? leader, bool? administrators, CancellationToken ct)
    {
        var camera = await Cameras.SingleOrDefaultAsync(x => x.Id == cameraId && x.OwnerPatientId == patientId, ct);
        if (camera is null)
            return FamilyCameraChange.NotFound;

        var membership = leader == true ? await GetMembershipAsync(patientId, ct) : null;
        if (leader == true && (membership is null || membership.LeaderId == patientId
            || !await ActivePatients.AnyAsync(x => x.Id == membership.LeaderId, ct)))
            return FamilyCameraChange.Conflict;

        if (leader is not null)
            camera.SharedMembershipId = membership?.Id;
        if (administrators is not null)
            camera.SharedWithAdministrators = administrators.Value;
        return await SaveChangeAsync(ct);
    }

    public async Task<bool> RemoveCameraAsync(Guid patientId, Guid cameraId, CancellationToken ct) =>
        await Cameras.Where(x => x.OwnerPatientId == patientId && x.Id == cameraId).ExecuteDeleteAsync(ct) > 0;

    public async Task<string?> FindAppFeedAsync(Guid patientId, Guid cameraId, CancellationToken ct)
    {
        var own = await Cameras.AsNoTracking().Where(x => x.Id == cameraId && x.OwnerPatientId == patientId)
            .Select(x => x.Provider).SingleOrDefaultAsync(ct);
        return own ?? await SharedWithLeader(patientId)
            .Where(x => x.Id == cameraId && ActivePatients.Any(owner => owner.Id == x.OwnerPatientId))
            .Select(x => x.Provider).SingleOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<FamilyCameraDto>?> ListAdminCamerasAsync(Guid patientId, Guid? clinicId, Guid? organizationId, CancellationToken ct)
    {
        var owner = await InAdminScope(patientId, clinicId, organizationId)
            .Select(x => new { x.FirstName, x.LastName }).SingleOrDefaultAsync(ct);
        if (owner is null)
            return null;

        var ownerName = owner.FirstName + " " + owner.LastName;
        return await Cameras.AsNoTracking().Where(x => x.OwnerPatientId == patientId && x.SharedWithAdministrators)
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new FamilyCameraDto(x.Id, x.Name, x.Provider, ownerName)).ToListAsync(ct);
    }

    public Task<string?> FindAdminFeedAsync(Guid patientId, Guid cameraId, Guid? clinicId, Guid? organizationId, CancellationToken ct) =>
        (from camera in Cameras.AsNoTracking()
         join owner in InAdminScope(patientId, clinicId, organizationId) on camera.OwnerPatientId equals owner.Id
         where camera.Id == cameraId && camera.SharedWithAdministrators
         select camera.Provider).SingleOrDefaultAsync(ct);

    private IQueryable<PatientProfile> InAdminScope(Guid patientId, Guid? clinicId, Guid? organizationId)
    {
        var patients = ActivePatients.Where(x => x.Id == patientId);
        if (clinicId != null)
            patients = patients.Where(x => x.ClinicId == clinicId);
        if (organizationId != null)
            patients = patients.Where(x => db.Clinics.Any(clinic => clinic.Id == x.ClinicId && clinic.OrganizationId == organizationId));
        return patients;
    }

    private IQueryable<FamilyCamera> SharedWithLeader(Guid leaderPatientId) =>
        from camera in Cameras.AsNoTracking()
        join member in Memberships.AsNoTracking() on camera.SharedMembershipId equals (Guid?)member.Id
        join team in Teams.AsNoTracking() on member.TeamId equals team.Id
        where team.LeaderPatientId == leaderPatientId && member.PatientId == camera.OwnerPatientId
        select camera;

    private Task<MembershipInfo?> GetMembershipAsync(Guid patientId, CancellationToken ct) =>
        (from member in Memberships.AsNoTracking()
         join team in Teams.AsNoTracking() on member.TeamId equals team.Id
         where member.PatientId == patientId
         select new MembershipInfo(member.Id, team.Id, team.Name, team.LeaderPatientId)).SingleOrDefaultAsync(ct);

    private async Task<FamilyCameraChange> SaveChangeAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return FamilyCameraChange.Applied;
        }
        catch (DbUpdateConcurrencyException)
        {
            return FamilyCameraChange.Conflict;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException postgres && IsWriteConflict(postgres))
        {
            return FamilyCameraChange.Conflict;
        }
    }

    private static string HashCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(code)));

    private static bool IsWriteConflict(PostgresException ex) =>
        ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation;

    private sealed record MembershipInfo(Guid Id, Guid TeamId, string TeamName, Guid LeaderId);
}
