using System.Text.Json;
using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using NSubstitute;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class ProgramUpcomingTests
{
    [Theory]
    [InlineData(12, 80, 2, 82)]
    [InlineData(12, 82, 0, 0)]
    [InlineData(83, 80, 6, 82)]
    public void ProximosSeisDias_RespetanDuracionYSinDia84EnProgramaNuevo(int weeks, int elapsed, int count, int first)
    {
        var start = new DateOnly(2026, 11, 2);
        var enrollment = new ProgramEnrollment { Id = Guid.NewGuid(), PatientId = Guid.NewGuid(), StartLocalDate = start,
            Template = new() { TotalWeeks = weeks, DayTemplates = Enumerable.Range(1, 7).Select(d => new WeeklyDayTemplate {
                Weekday = (short)d, TaskCode = TaskCode.ejercicio }).ToList() } };
        var data = new EffectiveContentData { Enrollments = [enrollment] };
        var resolver = new EffectiveDayContentResolver(Substitute.For<IEffectiveDayContentStore>());
        var days = ProgramUpcomingPlanner.Build(data, enrollment, resolver, start.AddDays(elapsed), CancellationToken.None);
        Assert.Equal(count, days.Count);
        if (count > 0) Assert.Equal(first, days[0].Day);
        if (weeks == 12) Assert.All(days, d => Assert.InRange(d.Day, 1, 83));
    }

    [Fact]
    public void Contrato_DeserializaSnapshotAnteriorYCambiosSonOpcionales()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string old = """{"enrollmentId":"b3e15d15-5027-43ec-9b44-e422a90eff27","template":{},"todayLocalDate":"2026-11-02","todayTasks":[],"todayPoints":0,"todayBonusAvailable":true,"todayPointsMax":70,"xp":{},"streak":{},"nextMilestoneDays":7,"calendar":[]}""";
        var snapshot = JsonSerializer.Deserialize<ProgramSnapshotDto>(old, options)!;
        Assert.Null(snapshot.Upcoming); Assert.Null(snapshot.Rest); Assert.Equal(70, snapshot.TodayPointsMax);
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, options));
        Assert.False(serialized.RootElement.TryGetProperty("upcoming", out _)); Assert.False(serialized.RootElement.TryGetProperty("rest", out _));
        using var rest = JsonDocument.Parse(JsonSerializer.Serialize(snapshot with { Rest = true,
            Upcoming = [new(new(2026, 11, 3), 2, 1, [], true)] }, options));
        Assert.True(rest.RootElement.GetProperty("rest").GetBoolean());
        Assert.True(rest.RootElement.GetProperty("upcoming")[0].GetProperty("rest").GetBoolean());
    }
}
