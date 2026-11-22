using System.Security.Claims;

namespace CoppAddresd.Api.Context;

internal static class PatientDirectoryScope
{
    public static async Task<(bool Allowed, Guid? OwnProfessionalId)> ResolveAsync(
        ClaimsPrincipal user,
        ICurrentContext context,
        CancellationToken ct
    )
    {
        // Los roles del Perfil 360 conservan asignación incluso con Patients.View.
        if (!user.IsInRole("Admin") && (user.IsInRole("ClinicalDirector")
            || user.IsInRole("NutritionReviewer") || user.IsInRole("CareCoordinator")
            || await context.HasPermissionAsync("Program.Profile360View", ct)))
        {
            var professional = await context.GetProfessionalIdAsync(ct);
            var allowed = await context.HasPermissionAsync("Patients.View", ct)
                || await context.HasPermissionAsync("Patients.ViewOwn", ct);
            return (allowed && professional is not null, professional);
        }
        if (await context.HasPermissionAsync("Patients.View", ct))
            return (true, null);

        if (await context.HasPermissionAsync("Patients.ViewOwn", ct))
        {
            var professional = await context.GetProfessionalIdAsync(ct);
            return (professional is not null, professional);
        }

        return (false, null);
    }
}
