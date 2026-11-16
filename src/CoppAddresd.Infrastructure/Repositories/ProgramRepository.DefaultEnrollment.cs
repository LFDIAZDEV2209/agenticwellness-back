using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Application.Features.ProgramProgress;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed partial class ProgramRepository
{
    /// <summary>Arranque determinista A2/F2, en la misma transacción que completa la batería inicial.</summary>
    public async Task EnsureDefaultEnrollmentAsync(Guid patientId, CancellationToken ct)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("El arranque requiere la transacción del submit.");
        var patient = await dbContext.PatientProfiles.FromSqlInterpolated(
            $"SELECT * FROM app.patient_profiles WHERE id = {patientId} FOR UPDATE").SingleAsync(ct);
        // A4: no reiniciar programas previos ni reabrir pausados/cerrados al repetir tests.
        if (await dbContext.ProgramEnrollments.AsNoTracking().AnyAsync(e => e.PatientId == patientId, ct)) return;
        var code = configuration["Program:DefaultTemplate:Code"] ?? "program-coppaddresd-83-days";
        var template = await dbContext.ProgramTemplates.AsNoTracking().Include(t => t.DayTemplates)
            .SingleOrDefaultAsync(t => t.Code == code && t.Status == TemplateStatus.Active, ct)
            ?? throw new BusinessRuleViolationException("DEFAULT_PROGRAM_UNAVAILABLE: falta la plantilla activa por defecto.");
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById(patient.Timezone)));
        var enrollment = new ProgramEnrollment
        {
            Id = Guid.NewGuid(), PatientId = patientId, TemplateId = template.Id, Template = template,
            Timezone = patient.Timezone, Status = ProgramEnrollmentStatus.Active,
            CurrentWeekNumber = 1, StartLocalDate = start, StartedAt = now, CreatedAt = now,
        };
        // Sin prescripciones ni propuestas que oculten huecos del respaldo. Se valida
        // el calendario real del paciente (incluido domingo de la semana 12).
        var store = new EffectiveDayContentStore(dbContext);
        var catalog = await store.LoadGeneralCatalogAsync(ct);
        var resolver = new EffectiveDayContentResolver(store);
        if (resolver.ResolveRange(catalog, enrollment, start,
                start.AddDays(EffectiveDayContentResolver.DurationDays(template) - 1), ct).Any(d => d.HasGaps))
            throw new BusinessRuleViolationException("DEFAULT_PROGRAM_CONTENT_GAPS: faltan nutrición, rutina o podcasts válidos por defecto.");

        // La plantilla se leyó sin tracking: no adjuntar sus entidades como nuevas.
        enrollment.Template = null;
        dbContext.ProgramEnrollments.Add(enrollment);
        dbContext.StreakStates.Add(new StreakState { EnrollmentId = enrollment.Id });
        var specific = template.DayTemplates.ToLookup(d => d.WeekNumber);
        for (var number = 1; number <= template.TotalWeeks; number++)
        {
            ct.ThrowIfCancellationRequested();
            var weekStart = start.AddDays((number - 1) * 7);
            var days = specific[null].Concat(specific[number])
                .Where(d => ProgramTemplateWeekRules.IsAllowedDay(template.TotalWeeks, number, d.Weekday))
                .GroupBy(d => (d.Weekday, d.TaskCode)).Select(g => g.OrderByDescending(d => d.WeekNumber.HasValue).First());
            dbContext.ProgramWeeks.Add(new ProgramWeek
            {
                Id = Guid.NewGuid(), EnrollmentId = enrollment.Id, WeekNumber = number,
                Status = number == 1 ? ProgramWeekStatus.Active : ProgramWeekStatus.Locked,
                WeekStartDateLocal = weekStart,
                WeekEndDateLocal = weekStart.AddDays(Math.Min(7, EffectiveDayContentResolver.DurationDays(template) - (number - 1) * 7) - 1),
                TasksSnapshot = number == 1 ? BuildSnapshot(days) : EmptySnapshot(), TemplateVersionAtStart = template.Version,
                ActivatedAt = number == 1 ? now : null, CreatedAt = now,
            });
        }
        // Futuras Locked se materializan al activar, conservando cambios editoriales A3/§5.1.
        // Sin purgas de puntajes ni escrituras de propuestas/IA en este camino obligatorio.
        await dbContext.SaveChangesAsync(ct);
    }
}
