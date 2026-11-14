using CoppAddresd.Application.Features.Nutrition.Cases;
using CoppAddresd.Domain.Entities;

namespace CoppAddresd.UnitTests.Nutrition;

public sealed class NutritionCycleCalendarTests
{
    private static NutritionCycle NovemberCycle => new() { Number = 2, StartDate = new(2026, 11, 5), EndDate = new(2026, 12, 4), State = "en_curso" };

    [Theory]
    [InlineData("2026-12-05T04:59:59Z", "en_curso")]
    [InlineData("2026-12-05T05:00:00Z", "por_revisar")]
    public void CierreTrasDiaTreinta_UsaMedianocheDelPaciente(string utc, string expected)
        => Assert.Equal(expected, NutritionCycleCalendar.State(NovemberCycle,
            NutritionStateMachine.Today(DateTimeOffset.Parse(utc), "America/Bogota")));

    [Fact]
    public void PublicacionTardia_DiciembreNueve_DejaCincoAOchoEnContinuidad()
    {
        var cycle = NovemberCycle;
        var previous = new NutritionPrescription { StartDate = cycle.StartDate, EndDate = cycle.EndDate };
        var late = NutritionStateMachine.Dates(new(2026, 12, 9), null, null, previous);
        Assert.Equal(new DateOnly(2026, 12, 9), late.Start); Assert.Equal(new DateOnly(2027, 1, 7), late.End);
        Assert.Equal(new DateOnly(2026, 12, 8), NutritionCycleCalendar.AttributedThrough(cycle, late.Start, new(2026, 12, 20)));
        Assert.True(NutritionCycleCalendar.IsContinuity(cycle, new(2026, 12, 6)));
        var timely = NutritionStateMachine.Dates(new(2026, 12, 3), null, null, previous);
        Assert.Equal(new DateOnly(2026, 12, 5), timely.Start);
    }

    [Fact]
    public void HoyNoviembreVeinte_NoIncluyeFuturos_BorradorNoTieneVentana()
    {
        Assert.Equal(new DateOnly(2026, 11, 20), NutritionCycleCalendar.AttributedThrough(NovemberCycle, null, new(2026, 11, 20)));
        Assert.Null(NutritionCycleCalendar.AttributedThrough(new(), null, new(2026, 11, 20)));
        var retired = NovemberCycle; retired.State = "interrumpido";
        retired.InterruptedAt = new(2026, 12, 6, 15, 0, 0, DateTimeKind.Utc);
        Assert.Equal("interrumpido", NutritionCycleCalendar.State(retired, new(2026, 12, 20)));
        Assert.Equal(new DateOnly(2026, 12, 6), NutritionCycleCalendar.AttributedThrough(retired, null, new(2026, 12, 20)));
    }
}
