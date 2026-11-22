using CoppAddresd.Api.Context;
using CoppAddresd.Application.Features.FamilyCameras;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CoppAddresd.Api.Controllers;

[ApiController]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/family-cameras")]
public sealed class FamilyCamerasController(
    ICurrentContext context,
    IFamilyCameraStore store,
    IEnumerable<IFamilyCameraFeed> feeds
) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<FamilyCameraWorkspace>> Get(CancellationToken ct)
    {
        if (await AppPatientAsync(ct) is not { } patient)
            return Forbid();

        return Ok(await store.GetWorkspaceAsync(patient, ct));
    }

    [HttpPost("team")]
    public async Task<IActionResult> CreateTeam(FamilyCameraNameRequest request, CancellationToken ct)
    {
        if (await AppPatientAsync(ct) is not { } patient)
            return Forbid();

        var name = request.Name?.Trim();
        if (name is not { Length: >= 2 and <= 80 })
            return BadRequest();

        return Change(await store.CreateTeamAsync(patient, name, ct));
    }

    [HttpPost("team/invitations")]
    [EnableRateLimiting("family-camera-invitation")]
    public async Task<ActionResult<FamilyTeamInvitationDto>> Invite(CancellationToken ct)
    {
        if (await AppPatientAsync(ct) is not { } patient)
            return Forbid();

        var invite = await store.InviteAsync(patient, ct);
        return invite is null ? Forbid() : Ok(invite);
    }

    [HttpPost("team/join")]
    [EnableRateLimiting("family-camera-invitation")]
    public async Task<IActionResult> Join(FamilyCameraJoinRequest request, CancellationToken ct)
    {
        if (await AppPatientAsync(ct) is not { } patient)
            return Forbid();

        var code = request.Code?.Trim().ToUpperInvariant();
        if (code is not { Length: 16 } || !code.All(Uri.IsHexDigit))
            return BadRequest();

        return Change(await store.JoinAsync(patient, code, ct));
    }

    [HttpDelete("team/membership")]
    public async Task<IActionResult> Leave(CancellationToken ct)
    {
        if (await AppPatientAsync(ct) is not { } patient)
            return Forbid();

        return Change(await store.LeaveAsync(patient, ct));
    }

    [HttpPost("demo")]
    public async Task<ActionResult<OwnFamilyCameraDto>> AddDemo(FamilyCameraNameRequest request, CancellationToken ct)
    {
        if (await AppPatientAsync(ct) is not { } patient)
            return Forbid();

        var name = request.Name?.Trim();
        if (name is not { Length: >= 2 and <= 80 })
            return BadRequest();

        return Ok(await store.AddDemoAsync(patient, name, ct));
    }

    [HttpPatch("{cameraId:guid}/sharing")]
    public async Task<IActionResult> SetSharing(Guid cameraId, FamilyCameraSharingRequest request, CancellationToken ct)
    {
        if (await AppPatientAsync(ct) is not { } patient)
            return Forbid();

        if (request.Leader is null && request.Administrators is null)
            return BadRequest();

        return Change(await store.SetSharingAsync(patient, cameraId, request.Leader, request.Administrators, ct));
    }

    [HttpDelete("{cameraId:guid}")]
    public async Task<IActionResult> Remove(Guid cameraId, CancellationToken ct)
    {
        if (await AppPatientAsync(ct) is not { } patient)
            return Forbid();

        return await store.RemoveCameraAsync(patient, cameraId, ct) ? NoContent() : NotFound();
    }

    [HttpGet("{cameraId:guid}/frame")]
    [EnableRateLimiting("family-camera-frame")]
    public async Task<ActionResult<FamilyCameraFrame>> AppFrame(Guid cameraId, CancellationToken ct)
    {
        if (await AppPatientAsync(ct) is not { } patient)
            return Forbid();

        return await ReadFrameAsync(cameraId, await store.FindAppFeedAsync(patient, cameraId, ct), ct);
    }

    [HttpGet("patients/{patientId:guid}")]
    public async Task<ActionResult<IReadOnlyList<FamilyCameraDto>>> AdminList(Guid patientId, CancellationToken ct)
    {
        if (!await IsAuthorizedAdminAsync(ct))
            return Forbid();

        var shared = await store.ListAdminCamerasAsync(patientId, context.ActiveClinicId, context.ActiveOrganizationId, ct);
        return shared is null ? NotFound() : Ok(shared);
    }

    [HttpGet("patients/{patientId:guid}/{cameraId:guid}/frame")]
    [EnableRateLimiting("family-camera-frame")]
    public async Task<ActionResult<FamilyCameraFrame>> AdminFrame(Guid patientId, Guid cameraId, CancellationToken ct)
    {
        if (!await IsAuthorizedAdminAsync(ct))
            return Forbid();

        return await ReadFrameAsync(cameraId,
            await store.FindAdminFeedAsync(patientId, cameraId, context.ActiveClinicId, context.ActiveOrganizationId, ct), ct);
    }

    private Task<Guid?> AppPatientAsync(CancellationToken ct) =>
        User.HasClaim("aud", "app") && context.UserId is { } userId
            ? store.FindActivePatientIdAsync(userId, ct)
            : Task.FromResult<Guid?>(null);

    private async Task<bool> IsAuthorizedAdminAsync(CancellationToken ct) =>
        User.HasClaim("aud", "erp") && User.IsInRole("Admin")
        && await context.HasPermissionAsync("Patients.View", ct);

    private async Task<ActionResult<FamilyCameraFrame>> ReadFrameAsync(Guid id, string? provider, CancellationToken ct)
    {
        if (provider is null)
            return NotFound();

        var feed = feeds.FirstOrDefault(x => x.Provider == provider);
        var frame = feed is null ? null : await feed.ReadAsync(id, ct);
        return frame is null ? StatusCode(StatusCodes.Status503ServiceUnavailable) : Ok(frame);
    }

    private IActionResult Change(FamilyCameraChange result) => result switch
    {
        FamilyCameraChange.Applied => NoContent(),
        FamilyCameraChange.NotFound => NotFound(),
        _ => Conflict(),
    };
}

public sealed record FamilyCameraNameRequest(string? Name);
public sealed record FamilyCameraJoinRequest(string? Code);
public sealed record FamilyCameraSharingRequest(bool? Leader, bool? Administrators);
