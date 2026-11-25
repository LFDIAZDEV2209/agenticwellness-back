using CoppAddresd.Application.Features.Wellness.Year365;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using NSubstitute;

namespace CoppAddresd.UnitTests.Wellness365;

public sealed class Year365ReportTests
{
    private readonly Guid patient = Guid.NewGuid();
    private readonly IYear365ReportStore store = Substitute.For<IYear365ReportStore>();
    private readonly IPatientScopeGuard scope = Substitute.For<IPatientScopeGuard>();
    private readonly DateOnly start = new(2027, 12, 30);

    private GetYear365ReportQueryHandler CreateHandler(DateTimeOffset? date = null) =>
        new(store, scope, new FixedTimeProvider(date ?? new DateTimeOffset(2028, 2, 3, 4, 30, 0, TimeSpan.Zero)));

    private void Seed(params Year365DailyLog[] logs)
    {
        store.FindProgramAsync(patient, default).Returns(new Year365Program(start, "America/Bogota"));
        store.ListDailyLogsAsync(patient, default).Returns(logs);
    }

    [Fact]
    public async Task PartialFirstAndLastMonthsIncludeDaysOneAnd365()
    {
        Seed();
        var handler = CreateHandler();
        var first = (await handler.Handle(new(patient, "2027-12"), default)).Report!;
        var last = (await handler.Handle(new(patient, "2028-12"), default)).Report!;
        Assert.Equal(new[] { 1, 2 }, first.Days.Select(x => x.Day));
        Assert.Equal(365, last.Days[^1].Day);
        Assert.Equal(start.AddDays(364), last.Days[^1].Date);
        Assert.All(last.Days, day => Assert.True(day.Future));
        Assert.Equal(13, first.Months.Count);
        Assert.Equal("2028-12", first.Months[^1]);
        Assert.Null(first.Summary.AdherencePercent);
    }

    [Fact]
    public async Task RecordedZeroAndMissingObservationsStayDistinct()
    {
        Seed(
            new(3, start.AddDays(2), 0m, 75m, null),
            new(6, start.AddDays(5), 0.83m, null, 84m));
        var id = Guid.NewGuid();
        store.ListEventsAsync(patient, 3, 33, default).Returns(new[] { new Year365StoredEvent(id, 4, "modo") });

        var result = (await CreateHandler().Handle(new(patient, "2028-01"), default)).Report!;
        Assert.Equal(0m, result.Days[0].AdherencePercent);
        Assert.Null(result.Days[1].AdherencePercent);
        Assert.Equal(2, result.Summary.RecordedDays);
        Assert.Equal(41.5m, result.Summary.AdherencePercent);
        Assert.NotNull(result.Summary.Weight);
        Assert.NotNull(result.Summary.Waist);
        Assert.Equal(new Year365Measurement(start.AddDays(2), 75m), result.Summary.Weight);
        Assert.Equal(new Year365Measurement(start.AddDays(5), 84m), result.Summary.Waist);
        Assert.Equal(new Year365Event(id, 4, start.AddDays(3), "modo"), Assert.Single(result.Events));
        Assert.Null(result.Summary.ScheduledPauseDays);
        Assert.Null(result.Summary.SafetyExercisePauseDays);
    }

    [Fact]
    public async Task StreakCrossesMonthAndStopsAtTheLocalCurrentDay()
    {
        Seed(
            new(1, start, 0.59m, null, null),
            new(2, start.AddDays(1), 0.60m, null, null),
            new(3, start.AddDays(2), 0.80m, null, null));
        var result = (await CreateHandler(new DateTimeOffset(2028, 1, 2, 4, 30, 0, TimeSpan.Zero))
            .Handle(new(patient, null), default)).Report!;
        Assert.Equal(new DateOnly(2028, 1, 1), result.Today);
        Assert.Equal("2028-01", result.Month);
        Assert.Equal(2, result.Summary.StreakDays);
        Assert.True(result.Days[1].Future);
    }

    [Fact]
    public async Task MissingCutoffDayStopsStreakAndLeapDayIsIncluded()
    {
        var knownDate = new DateOnly(2028, 2, 28);
        Seed(new Year365DailyLog(knownDate.DayNumber - start.DayNumber + 1, knownDate, 0.85m, null, null));
        var result = (await CreateHandler(new DateTimeOffset(2028, 2, 29, 16, 0, 0, TimeSpan.Zero))
            .Handle(new(patient, "2028-02"), default)).Report!;
        Assert.Equal(29, result.Days.Count);
        Assert.Equal(0, result.Summary.StreakDays);
    }

    [Fact]
    public async Task UnavailableTimezoneLeavesTheDateCutoffAndFutureStatusUnknown()
    {
        Seed(new Year365DailyLog(3, start.AddDays(2), 0.7m, null, null));
        store.FindProgramAsync(patient, default).Returns(new Year365Program(start, "Invalid/TimeZone"));
        var result = (await CreateHandler().Handle(new(patient, null), default)).Report!;
        Assert.Equal("2028-01", result.Month);
        Assert.Null(result.Today);
        Assert.Null(result.Summary.StreakDays);
        Assert.All(result.Days, day => Assert.Null(day.Future));
    }

    [Theory]
    [InlineData("2028-2")]
    [InlineData("2028-13")]
    [InlineData("2029-01")]
    public async Task InvalidOrOutsideMonthIsRejected(string month)
    {
        Seed();
        await Assert.ThrowsAsync<ValidationException>(() => CreateHandler().Handle(new(patient, month), default));
    }

    [Fact]
    public async Task MismatchedDayAndDateFailsRatherThanMovingObservations()
    {
        Seed(new Year365DailyLog(5, start.AddDays(1), 0.9m, 74m, null));
        await Assert.ThrowsAsync<BusinessRuleViolationException>(
            () => CreateHandler().Handle(new(patient, "2027-12"), default));
    }

    [Fact]
    public async Task MissingProgramReturnsEmptyOnlyAfterCheckingPatientScope()
    {
        Assert.Null((await CreateHandler().Handle(new(patient, null), default)).Report);
        await scope.Received(1).EnsureAsync(patient, default);
        scope.EnsureAsync(patient, default).Returns(Task.FromException(new NotFoundException("Recurso no encontrado.")));
        await Assert.ThrowsAsync<NotFoundException>(() => CreateHandler().Handle(new(patient, null), default));
        await store.Received(1).FindProgramAsync(patient, default);
    }

    private sealed class FixedTimeProvider(DateTimeOffset date) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => date;
    }
}
