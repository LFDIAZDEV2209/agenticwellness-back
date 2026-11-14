using System.Data.Common;
using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.Nutrition.Queries.GetMyNutritionPlan;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Enums.HealthTests;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Application.Features.Agents.Vita;

public sealed record GetVitaContextQuery(Guid UserId, string Scope, string? ResourceVersion = null)
    : IRequest<VitaContextDto>;

public sealed class GetVitaContextQueryValidator : AbstractValidator<GetVitaContextQuery>
{
    public static readonly string[] Scopes = ["day", "nutrition", "assessments", "podcast", "exercise", "progress"];
    public GetVitaContextQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Scope).Must(x => Scopes.Contains(x));
        RuleFor(x => x.ResourceVersion).MaximumLength(64);
    }
}

/// <summary>Compone casos de uso existentes, secuenciales por DbContext scoped, sin mutaciones.</summary>
public sealed class GetVitaContextQueryHandler(VitaPatientAccess access, IProgramRepository programs,
    IWellnessRepository wellness, INutritionPrescriptionRepository prescriptions,
    IProgramContentResolver content, IHealthTestRepository tests, INutritionPlanWorkflow nutritionStatus,
    IPatientMeasurementRepository devices, TimeProvider clock,
    ILogger<GetVitaContextQueryHandler> logger) : IRequestHandler<GetVitaContextQuery, VitaContextDto>
{
    public async Task<VitaContextDto> Handle(GetVitaContextQuery request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DateOnly? localDate = null;
        try
        {
            var patient = await access.RequireAsync(request.UserId, "chat", ct);
            localDate = access.Today(patient.Timezone);
            object? data;
            List<(string Destination, Guid? Id)> destinations = [];
            string source;
            if (request.Scope == "nutrition")
            {
                var plan = await GetMyNutritionPlanQueryHandler.ReadForPatientAsync(patient, wellness,
                    logger, prescriptions, clock, ct, content);
                // Misma selección que el contexto de paciente existente para el menú legado de siete días.
                var menuDay = plan is { Days.Count: > 0, CurrentDay: > 0 }
                    ? plan.Days.Any(d => d.DayNumber == plan.CurrentDay) ? plan.CurrentDay
                        : (plan.CurrentDay - 1) % plan.Days.Count + 1 : null;
                var day = plan?.Days.FirstOrDefault(d => d.DayNumber == menuDay);
                if (plan is null || day is null)
                {
                    var availability = await nutritionStatus.GetStatusAsync(request.UserId, patient.Timezone, ct);
                    return Response(request.ResourceVersion is null ? "empty" : "stale",
                        new { Availability = availability.Status }, null, [], [new("nutrition", "INutritionPlanWorkflow.GetStatusAsync")]);
                }
                data = plan is null || day is null ? null : new { plan.Id, plan.Name, plan.Status,
                    plan.Revision, plan.StartDate, plan.EndDate, plan.CurrentDay, plan.Disclaimer,
                    plan.DailyCalorieTarget, plan.DailyProteinTarget, plan.DailyCarbsTarget,
                    plan.DailyFatTarget, plan.DailyFiberTarget, day.DayNumber, day.DailyWaterMl, day.Meals };
                if (data is not null) destinations.Add(("nutrition", plan!.Id));
                source = "GetMyNutritionPlanQuery";
            }
            else if (request.Scope == "assessments")
            {
                // No llamar al controller: su fallback asigna baterías al leer.
                var assignments = await tests.ListAssignmentsByPatientAsync(patient.Id, ct);
                var visible = assignments.Where(a => a.PatientId == patient.Id
                    && a.Status is HealthTestAssignmentStatus.pending or HealthTestAssignmentStatus.in_progress or HealthTestAssignmentStatus.completed)
                    .OrderBy(a => a.DueDate).ThenBy(a => a.Id).ToArray();
                var items = visible.Take(100).Select(a => new { a.Id, a.VersionId,
                    TestName = a.Version?.Instrument?.Name ?? a.Version?.Name,
                    TestCode = a.Version?.Instrument?.Code, Status = a.Status.ToString(),
                    a.DueDate, a.ExpiresAt, a.CompletedAt }).ToArray();
                data = items.Length == 0 ? null : new { Assignments = items, HasMore = visible.Length > items.Length };
                foreach (var item in items.Where(a => a.Status is "pending" or "in_progress"))
                    destinations.Add(("assessments", item.Id));
                source = "IHealthTestRepository.ListAssignmentsByPatientAsync";
            }
            else if (request.Scope == "progress")
            {
                // Semana ISO local, no UTC. Una lectura de dispositivo; no ficha clínica ni consultas por día.
                var zone = TimeZoneInfo.FindSystemTimeZoneById(patient.Timezone);
                var from = localDate.Value.AddDays(-((int)localDate.Value.DayOfWeek + 6) % 7);
                var fromUtc = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), zone);
                var toUtc = clock.GetUtcNow().UtcDateTime;
                var rows = await devices.GetDeviceWindowAsync(patient.Id, fromUtc, toUtc, ct);
                var metrics = rows.Where(m => m.Source == "device" && m.MetricCode is "step_count" or "sleep_minutes" or "activity_kcal")
                    .GroupBy(m => (m.MetricCode, Date: DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(m.ObservedAt, zone).DateTime)))
                    .Select(g => g.OrderByDescending(m => m.ObservedAt).ThenByDescending(m => m.Id).First())
                    .OrderBy(m => m.ObservedAt).ThenBy(m => m.MetricCode, StringComparer.Ordinal)
                    .Select(m => new { m.MetricCode, m.Value, m.UnitCode, m.UnitSymbol, m.ObservedAt, m.Source,
                        LocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(m.ObservedAt, zone).DateTime) }).ToArray();
                var enrollment = await programs.GetActiveEnrollmentForPatientAsync(patient.Id, ct);
                if (enrollment is not null && enrollment.PatientId != patient.Id) throw new ForbiddenException("VITA_PATIENT_UNAVAILABLE");
                var snapshot = enrollment is null ? null : await programs.GetSnapshotAsync(enrollment.Id,
                    await programs.GetPatientLocalTodayAsync(enrollment.Id, ct) ?? localDate.Value, ct);
                data = snapshot is null && metrics.Length == 0 ? null : new
                {
                    FromLocalDate = from, ToLocalDate = localDate, FromUtc = fromUtc,
                    // El final de la ventana se refleja en asOf; no cambia la revisión cada milisegundo.
                    Timezone = patient.Timezone, DeviceAvailability = metrics.Length == 0 ? "empty" : "ok",
                    Aggregation = "latest-per-metric-local-day", DeviceMetrics = metrics,
                    Program = snapshot is null ? null : new { snapshot.Xp, snapshot.Streak,
                        Calendar = snapshot.Calendar.Where(d => d.LocalDate >= from && d.LocalDate <= localDate).ToArray(),
                        snapshot.TodayPoints, snapshot.TodayPointsMax }
                };
                if (data is not null) destinations.Add(("progress", null));
                source = "GetSnapshotQuery + IPatientMeasurementRepository.GetDeviceWindowAsync";
            }
            else
            {
                var enrollment = await programs.GetActiveEnrollmentForPatientAsync(patient.Id, ct);
                if (enrollment is null) return Empty();
                if (enrollment.PatientId != patient.Id) throw new ForbiddenException("VITA_PATIENT_UNAVAILABLE");
                localDate = await programs.GetPatientLocalTodayAsync(enrollment.Id, ct)
                    ?? throw new NotFoundException("NO_ACTIVE_ENROLLMENT");
                var snapshot = await programs.GetSnapshotAsync(enrollment.Id, localDate.Value, ct);
                if (snapshot is null) return Empty();
                if (snapshot.EnrollmentId != enrollment.Id || snapshot.TodayLocalDate != localDate)
                    return Response("stale", null, null, [], []);
                var activeWeek = localDate >= snapshot.Template.CurrentWeekStartDateLocal
                    && localDate <= snapshot.Template.CurrentWeekEndDateLocal
                    && snapshot.Template.CurrentWeekStatus == ProgramWeekStatus.Active;
                // No presentar tareas de otra semana como si fueran las de hoy ni activar semanas.
                if (!activeWeek && request.Scope is "day" or "exercise" or "podcast") return Empty();
                data = request.Scope switch
                {
                    "day" => new VitaDayDto(enrollment.Id, snapshot.Template.CurrentWeekNumber,
                        snapshot.Template.CurrentWeekStatus.ToString(), snapshot.Rest == true,
                        snapshot.TodayTasks.Select(t => new VitaTaskDto(t.TaskCode.ToString(), t.Title,
                            t.Status, t.ContentUnavailable, ResourceId(t))).ToArray()),
                    "podcast" => Podcast(snapshot),
                    "exercise" => Exercise(snapshot),
                    _ => throw new ValidationException("Scope de Vita desconocido.")
                };
                if (data is not null && request.Scope != "exercise")
                    destinations.Add((request.Scope == "day" ? "program" : request.Scope,
                        data is VitaPodcastDto podcast ? podcast.MediaId : null));
                if (data is VitaExerciseDto { Rest: false } exercise) destinations.Add(("exercise", exercise.RoutineId));
                source = "GetSnapshotQuery";
            }
            if (data is null) return Empty();
            var version = VitaVersions.Hash(new { Scope = request.Scope, LocalDate = localDate, Data = data });
            if (request.ResourceVersion is not null && request.ResourceVersion != version)
                return Response("stale", null, version, [], [new(request.Scope, source)]);
            return Response("ok", data, version, destinations.Select(d => new VitaActionDto(
                "navigate", d.Destination, d.Id, localDate!.Value, version)).ToArray(), [new(request.Scope, source)]);
        }
        catch (ForbiddenException) { return Response("forbidden", null, null, [], []); }
        catch (AiConsentException ex) { return Response(ex.StatusCode == 503 ? "unavailable" : "forbidden", null, null, [], []); }
        catch (NotFoundException) { return Empty(); }
        catch (Exception ex) when (ex is DbException or HttpRequestException or TimeoutException or TimeZoneNotFoundException or InvalidTimeZoneException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Vita: capacidad {Scope} no disponible ({ErrorType})", request.Scope, ex.GetType().Name);
            return Response("unavailable", null, null, [], []);
        }

        VitaContextDto Empty() => Response(request.ResourceVersion is null ? "empty" : "stale", null, null, [],
            [new(request.Scope, "lectura sin recurso efectivo")]);
        VitaContextDto Response(string status, object? value, string? version,
            IReadOnlyList<VitaActionDto> actions, IReadOnlyList<VitaSourceDto> sources) =>
            new(status, clock.GetUtcNow(), localDate, version,
                value is null ? null : VitaVersions.Json(value), actions, sources);
    }

    public static Guid? ResourceId(TodayTaskDto task) => task.Content?.MediaId
        ?? task.Content?.ExerciseRoutineId ?? task.Content?.SourcePrescriptionId ?? task.Content?.NutritionPlanId;

    private static VitaPodcastDto? Podcast(ProgramSnapshotDto snapshot)
    {
        var task = snapshot.TodayTasks.FirstOrDefault(t => t.TaskCode == TaskCode.podcast);
        return task is { ContentUnavailable: false, Content: { ContentUnavailable: false, MediaId: { } id } c }
            ? new(id, c.Title, c.Description, c.Author, c.DurationSecs, c.Chapters, c.Takeaways) : null;
    }

    private static VitaExerciseDto? Exercise(ProgramSnapshotDto snapshot)
    {
        if (snapshot.Rest == true) return new(true, null, null, []);
        var task = snapshot.TodayTasks.FirstOrDefault(t => t.TaskCode == TaskCode.ejercicio);
        if (task?.Content?.Rest == true) return new(true, null, null, []);
        return task is { ContentUnavailable: false, Content: { ContentUnavailable: false, ExerciseRoutineId: { } id } c }
            ? new(false, id, c.ExerciseRoutineName, c.Exercises?.Select(e => new VitaExerciseItemDto(
                e.Name, e.Description, e.Sets, e.Repetitions, e.DurationSecs, e.RestSeconds,
                e.Tips, e.WeightKg, e.Tempo, e.Rpe)).ToArray() ?? []) : null;
    }
}
