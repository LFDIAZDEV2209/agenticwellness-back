using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using NSubstitute;

namespace CoppAddresd.UnitTests.Nutrition;

public sealed class NutritionCycleAdherenceTests
{
    [Fact]
    public void SeisSieteOcho_PromediaSoloDiasConDatos_YSeparaCobertura()
    {
        var patient = Guid.NewGuid(); var id = Guid.NewGuid(); var start = new DateOnly(2026, 11, 5);
        var header = new NutritionCycleReadHeader(new() { PatientId = patient, Number = 2, StartDate = start, EndDate = start.AddDays(29), State = "en_curso" }, "America/Bogota", null);
        var version = new EffectivePrescription(id, patient, start, start.AddDays(29), start.ToDateTime(new(15, 0), DateTimeKind.Utc), [2, 3, 4]);
        var logs = new[] { "des", "alm", "cen" }.Select(m => new CycleIntake(Guid.NewGuid(), start.AddDays(1), m))
            .Concat(new[] { "des", "alm" }.Select(m => new CycleIntake(Guid.NewGuid(), start.AddDays(2), m))).ToArray();
        var data = new NutritionCycleReadData([version], [new(id, 2, 3), new(id, 3, 3), new(id, 4, 3)], logs);
        var result = NutritionCycleAdherence.Calculate(header, data, new(2026, 11, 6), new(2026, 11, 8), new(2026, 11, 9), 1, 30, default);
        Assert.Equal(.83m, result.Adherence); Assert.Equal(2, result.DaysWithData); Assert.Equal(3, result.ElapsedDays);
        Assert.Equal(1, result.Interruptions); Assert.Null(result.Days[2].Adherence);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 1, 1, 0)]
    [InlineData(3, 1, 1, 0)]
    public void HoyEnCurso_VacioNoEsInterrupcion_ConRegistrosCuentaParcial(int meals, int measured, int elapsed, int interruptions)
    {
        var today = new DateOnly(2026, 11, 14); var patient = Guid.NewGuid(); var prescription = Guid.NewGuid();
        var header = new NutritionCycleReadHeader(new() { PatientId = patient, Number = 1, StartDate = today, EndDate = today.AddDays(29), State = "en_curso" }, "America/Bogota", null);
        var version = new EffectivePrescription(prescription, patient, today, today.AddDays(29), today.ToDateTime(new(5, 0), DateTimeKind.Utc), [1]);
        var logs = new[] { "des", "alm", "cen" }.Take(meals).Select(m => new CycleIntake(Guid.NewGuid(), today, m)).ToArray();
        var data = new NutritionCycleReadData([version], [new(prescription, 1, 3)], logs);
        var result = NutritionCycleAdherence.Calculate(header, data, today, today, today, 1, 30, default);
        Assert.Equal(measured, result.DaysWithData); Assert.Equal(elapsed, result.ElapsedDays); Assert.Equal(interruptions, result.Interruptions);
        Assert.False(Assert.Single(result.Days).Interruption); Assert.Equal(today, result.Through);
        if (meals == 0) Assert.Null(result.Adherence);
        else Assert.Equal(decimal.Round((decimal)meals / 3, 2), result.Adherence);
        var closed = NutritionCycleAdherence.Calculate(header, data, today, today, today.AddDays(1), 1, 30, default);
        Assert.Equal(1, closed.ElapsedDays); Assert.Equal(meals == 0 ? 1 : 0, closed.Interruptions);
    }

    [Fact]
    public async Task SinAlcance_NoLeeCabeceraNiRegistros()
    {
        var guard = Substitute.For<IPatientScopeGuard>(); var store = Substitute.For<INutritionCycleReadStore>(); var id = Guid.NewGuid();
        guard.EnsureResourceAsync(PatientResource.Cycle, id, "Nutrition.View", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Guid>(new NotFoundException("Recurso no encontrado.")));
        await Assert.ThrowsAsync<NotFoundException>(() => new GetCycleAdherenceQueryHandler(guard, store, TimeProvider.System).Handle(new(id), default));
        Assert.Empty(store.ReceivedCalls());
    }
}
