using System.Text.Json;
using CoppAddresd.Application.Features.Nutrition.Cases;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Application.Features.Nutrition.Commands;

public sealed record GenerateCycleAiDraft(Guid CycleId, int Revision = 0, string Language = "es") : IRequest<GenerateCycleAiDraftResult>;

/// <summary>Snapshot de lectura, llamada sin transacción y persistencia optimista tras revalidar el alcance.</summary>
public sealed class GenerateCycleAiDraftHandler(IPatientScopeGuard scope, IPatientScopeActor actor,
    INutritionCycleReviewStore reviews, INutritionCycleReadStore reading, INutritionCycleAiReadStore data,
    INutritionCycleSummaryClient client, TimeProvider clock, ILogger<GenerateCycleAiDraftHandler> logger)
    : IRequestHandler<GenerateCycleAiDraft, GenerateCycleAiDraftResult>
{
    public async Task<GenerateCycleAiDraftResult> Handle(GenerateCycleAiDraft request, CancellationToken ct)
    {
        var patient = await AuthorizeAsync(request.CycleId, ct);
        if (request.Revision < 0 || request.Language is not ("es" or "en")) throw new ValidationException("Revisión o idioma inválidos.");
        var author = actor.UserId ?? throw new ForbiddenException("Falta identidad profesional.");
        var capturedAt = clock.GetUtcNow();
        var input = await reading.ReadAsync(async readCt =>
        {
            CycleReviewHandler.EnsureEditable(await reviews.LoadAsync(request.CycleId, readCt), request.Revision);
            var header = await reading.HeaderAsync(request.CycleId, patient, readCt) ?? throw new NotFoundException("Ciclo no encontrado.");
            var today = NutritionStateMachine.Today(capturedAt, header.Timezone);
            var end = NutritionCycleCalendar.AttributedThrough(header.Cycle, header.SuccessorStart, today, header.Timezone);
            if (header.Cycle.Number is not { } number || header.Cycle.StartDate is not { } start || end is null)
                throw new UnprocessableEntityException("El ciclo aún no ha comenzado.");
            if (end.Value.DayNumber - start.DayNumber >= 366) return null;
            var period = await data.PeriodAsync(patient, start, end.Value, true, readCt);
            var averages = await reviews.AveragesAsync(patient, start, end.Value, readCt);
            CycleAiAggregates? previous = null;
            if (await data.PreviousAsync(patient, number, readCt) is { } predecessor)
            {
                var prior = await reading.HeaderAsync(predecessor.Id, patient, readCt);
                var priorEnd = NutritionCycleCalendar.AttributedThrough(predecessor, prior?.SuccessorStart, today, header.Timezone);
                if (predecessor.StartDate is { } priorStart && priorEnd is { } last && last.DayNumber - priorStart.DayNumber < 366)
                    previous = Aggregates(await data.PeriodAsync(patient, priorStart, last, false, readCt),
                        await reviews.AveragesAsync(patient, priorStart, last, readCt), priorStart, last);
            }
            // Límite exclusivo al final del día local atribuido; no se envían mediciones del futuro.
            var cutoff = ThroughExclusiveUtc(end.Value, header.Timezone);
            if (cutoff > capturedAt.UtcDateTime) cutoff = capturedAt.UtcDateTime.AddTicks(1);
            return new NutritionCycleSummaryInput(number, start, end.Value, request.Language,
                Aggregates(period, averages, start, end.Value), period.MealCounts, previous,
                await data.MeasurementsAsync(patient, cutoff, readCt), period.Samples.Select(s =>
                    new CycleAiRecord(s.Date, s.MealCode, s.Recorded.Calories,
                        new(s.Recorded.ProteinG, s.Recorded.CarbsG, s.Recorded.FatG, s.Recorded.FiberG),
                        NutritionIntakeOrigin.Resolve(s.Source, s.Recorded, s.OwnedEstimate), s.WaterMl)).ToArray())
                { SubjectPatientId = patient };
        }, ct);
        if (input is null) return Unavailable(request, "ai_period_too_long");
        NutritionCycleSummaryResponse response;
        try { response = await client.GenerateAsync(input, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (NutritionCycleAiUnavailableException ex)
        {
            logger.LogWarning("Nutrition.CycleAiUnavailable Code={Code}", ex.Code);
            return Unavailable(request, ex.Code);
        }
        // La red puede haber tardado: no persistir datos tras revocación o edición/publicación concurrente.
        await AuthorizeAsync(request.CycleId, ct);
        var draft = new NutritionCycleAiDraft(response.Status, response.Summary, response.Patterns, response.Gaps,
            response.Draft, response.Model, response.GeneratedAt, capturedAt, NutritionCycleAiJson.Hash(input), input);
        var revision = await reviews.WithPatientLockAsync(patient, async writeCt =>
        {
            var review = await reviews.LoadAsync(request.CycleId, writeCt);
            CycleReviewHandler.EnsureEditable(review, request.Revision);
            review ??= new NutritionCycleReview { CycleId = request.CycleId, ReviewerId = author,
                ReviewedAt = clock.GetUtcNow().UtcDateTime, ProfessionalName = await reviews.AuthorNameAsync(author, writeCt) };
            review.AiDraftJson = JsonSerializer.Serialize(draft, NutritionCycleAiJson.Options);
            review.AiDraftConfirmed = false; review.Revision++;
            await reviews.SaveAsync(review, writeCt); await reviews.AuditAsync(review, author, "NutritionCycleAiDraftGenerated", writeCt);
            return review.Revision;
        }, ct);
        return response.Status == "insufficient"
            ? new("insufficient", revision, draft, "ai_insufficient_data", request.Language == "en"
                ? "Not enough nutrition data. You can continue the manual review." : "No hay datos nutricionales suficientes. Puedes continuar la revisión manual.")
            : new("ok", revision, draft);
    }

    private async Task<Guid> AuthorizeAsync(Guid cycle, CancellationToken ct)
    {
        var patient = await scope.EnsureResourceAsync(PatientResource.Cycle, cycle, "Nutrition.Approve", ct);
        await scope.EnsureAsync(patient, "Nutrition.NotesView", ct);
        await scope.EnsureAsync(patient, "Nutrition.RecordsView", ct);
        return patient;
    }
    private static CycleAiAggregates Aggregates(CycleAiPeriodData data, CycleIntakeAverages averages, DateOnly start, DateOnly end)
        => new(data.DaysWithRecords, decimal.Round((decimal)data.DaysWithRecords / (end.DayNumber - start.DayNumber + 1), 4),
            averages.Calories, averages.ProteinG, averages.CarbsG, averages.FatG, averages.FiberG, averages.WaterMl);
    private static GenerateCycleAiDraftResult Unavailable(GenerateCycleAiDraft request, string code)
        => new("unavailable", request.Revision, null, code, request.Language == "en"
            ? "The AI summary is unavailable. You can continue the manual review."
            : "El resumen de IA no está disponible. Puedes continuar la revisión manual.");

    private static DateTime ThroughExclusiveUtc(DateOnly day, string timezone)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
        var next = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        // Hay zonas que adelantan o repiten la medianoche: usar el primer instante válido del día siguiente.
        while (zone.IsInvalidTime(next)) next = next.AddMinutes(1);
        return zone.IsAmbiguousTime(next) ? new DateTimeOffset(next, zone.GetAmbiguousTimeOffsets(next).Max()).UtcDateTime
            : TimeZoneInfo.ConvertTimeToUtc(next, zone);
    }
}
