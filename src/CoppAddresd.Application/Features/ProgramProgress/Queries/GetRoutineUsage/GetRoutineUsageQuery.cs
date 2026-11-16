using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services.ProgramProgress;
using MediatR;

namespace CoppAddresd.Application.Features.ProgramProgress.Queries.GetRoutineUsage;

/// <summary>Uso efectivo actual dentro del alcance del actor; no cuenta historia completada ni semanas Locked.</summary>
public sealed record RoutineUsageDto(Guid RoutineId, int PatientCount, int EnrollmentCount, int ActiveWeekCount);

public sealed record GetRoutineUsageQuery(Guid RoutineId) : IRequest<RoutineUsageDto?>;

/// <summary>Lectura mínima para reutilizar el resolver canónico, sin prescripciones, comidas ni detalles clínicos.</summary>
public interface IProgramRoutineUsageStore
{
    Task<EffectiveContentData?> LoadAsync(Guid routineId, PatientScopeFilter scope, CancellationToken ct);
}

public sealed class GetRoutineUsageQueryHandler(IPatientScopeGuard scope, IProgramRoutineUsageStore store,
    IEffectiveDayContentResolver resolver) : IRequestHandler<GetRoutineUsageQuery, RoutineUsageDto?>
{
    public async Task<RoutineUsageDto?> Handle(GetRoutineUsageQuery request, CancellationToken ct)
    {
        var filter = await scope.GetFilterAsync("Wellness.View", ct);
        var data = await store.LoadAsync(request.RoutineId, filter, ct);
        if (data is null) return null;
        if (!data.ActiveRoutines.Contains(request.RoutineId)) return new(request.RoutineId, 0, 0, 0);
        var enrollments = data.Enrollments.ToDictionary(e => e.Id);
        var legacy = data.Legacy.ToLookup(a => a.PatientId);
        var patients = new HashSet<Guid>(); var usedEnrollments = new HashSet<Guid>(); var weeks = 0;
        foreach (var group in data.Weeks.GroupBy(w => w.EnrollmentId))
        {
            ct.ThrowIfCancellationRequested();
            var enrollment = enrollments[group.Key];
            var local = data with { Enrollments = [enrollment], Weeks = group.ToArray(), Legacy = legacy[enrollment.PatientId].ToArray() };
            foreach (var week in group)
                if (resolver.ResolveRange(local, enrollment, week.WeekStartDateLocal, week.WeekEndDateLocal, ct)
                    .Any(day => !day.Routine.Rest && day.Routine.Id == request.RoutineId))
                {
                    weeks++; patients.Add(enrollment.PatientId); usedEnrollments.Add(enrollment.Id);
                }
        }
        return new(request.RoutineId, patients.Count, usedEnrollments.Count, weeks);
    }
}
