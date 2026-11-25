using System.Globalization;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Domain.Wellness365;
using FluentValidation;
using FluentValidation.Results;
using MediatR;

namespace CoppAddresd.Application.Features.Wellness.Year365;

public sealed record Year365ReportResponse(Year365Report? Report);

public sealed record Year365Report(
    DateOnly StartDate,
    DateOnly EndDate,
    DateOnly? Today,
    IReadOnlyList<string> Months,
    string Month,
    IReadOnlyList<Year365Day> Days,
    Year365Summary Summary,
    IReadOnlyList<Year365Event> Events);

public sealed record Year365Day(
    int Day, DateOnly Date, bool? Future, decimal? AdherencePercent, decimal? WeightKg, decimal? WaistCm);

public sealed record Year365Measurement(DateOnly Date, decimal Value);

public sealed record Year365Summary(
    int RecordedDays,
    decimal? AdherencePercent,
    Year365Measurement? Weight,
    Year365Measurement? Waist,
    int? StreakDays,
    int? ScheduledPauseDays = null,
    int? SafetyExercisePauseDays = null);

public sealed record Year365Event(Guid Id, int Day, DateOnly Date, string Kind);
public sealed record Year365Program(DateOnly StartDate, string? TimeZone);
public sealed record Year365DailyLog(int Day, DateOnly Date, decimal Adherence, decimal? WeightKg, decimal? WaistCm);
public sealed record Year365StoredEvent(Guid Id, int Day, string Kind);

public sealed record GetYear365ReportQuery(Guid PatientId, string? Month) : IRequest<Year365ReportResponse>;

public sealed class GetYear365ReportQueryHandler(
    IYear365ReportStore store, IPatientScopeGuard scope, TimeProvider clock)
    : IRequestHandler<GetYear365ReportQuery, Year365ReportResponse>
{
    public async Task<Year365ReportResponse> Handle(GetYear365ReportQuery request, CancellationToken ct)
    {
        await scope.EnsureAsync(request.PatientId, ct);
        var requestedMonth = ParseMonth(request.Month);
        var program = await store.FindProgramAsync(request.PatientId, ct);
        if (program is null)
            return new(null);

        var start = program.StartDate;
        var end = start.AddDays(WellnessEngine.PlanLength - 1);
        var today = GetLocalToday(program.TimeZone);
        var logs = await store.ListDailyLogsAsync(request.PatientId, ct);
        var logsByDate = IndexLogs(start, end, logs);
        var latest = logs.Count == 0 ? start : logs.Max(x => x.Date);
        var selectedDate = today ?? latest;
        var month = requestedMonth ?? FirstOfMonth(selectedDate < start ? start : selectedDate > end ? end : selectedDate);
        if (month < FirstOfMonth(start) || month > FirstOfMonth(end))
            throw InvalidMonth("El mes está fuera del programa.");

        var firstDate = month < start ? start : month;
        var lastDateOfMonth = month.AddDays(DateTime.DaysInMonth(month.Year, month.Month) - 1);
        var lastDate = lastDateOfMonth < end ? lastDateOfMonth : end;
        var firstDay = firstDate.DayNumber - start.DayNumber + 1;
        var dayCount = lastDate.DayNumber - firstDate.DayNumber + 1;
        var days = Enumerable.Range(0, dayCount).Select(offset =>
        {
            var date = firstDate.AddDays(offset);
            var log = logsByDate.GetValueOrDefault(date);
            return new Year365Day(
                firstDay + offset, date, today.HasValue ? date > today.Value : null,
                log?.Adherence * 100, log?.WeightKg, log?.WaistCm);
        }).ToArray();

        var events = await store.ListEventsAsync(request.PatientId, firstDay, firstDay + dayCount - 1, ct);
        var months = new List<string>();
        for (var current = FirstOfMonth(start); current <= FirstOfMonth(end); current = current.AddMonths(1))
            months.Add(MonthKey(current));

        var summary = Summarize(days, logsByDate, firstDate, lastDate, today);
        return new(new Year365Report(
            start, end, today, months, MonthKey(month), days, summary,
            events.Select(x => new Year365Event(x.Id, x.Day, start.AddDays(x.Day - 1), x.Kind)).ToArray()));
    }

    private DateOnly? GetLocalToday(string? timeZone)
    {
        if (!string.IsNullOrWhiteSpace(timeZone) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone))
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
        return null;
    }

    private static Dictionary<DateOnly, Year365DailyLog> IndexLogs(
        DateOnly start, DateOnly end, IReadOnlyList<Year365DailyLog> logs)
    {
        var indexed = new Dictionary<DateOnly, Year365DailyLog>();
        foreach (var log in logs)
        {
            if (log.Date < start || log.Date > end
                || log.Day != log.Date.DayNumber - start.DayNumber + 1
                || !indexed.TryAdd(log.Date, log))
                throw new BusinessRuleViolationException("Las fechas del programa no coinciden con el historial.");
        }
        return indexed;
    }

    private static Year365Summary Summarize(
        IReadOnlyList<Year365Day> days, IReadOnlyDictionary<DateOnly, Year365DailyLog> logs,
        DateOnly firstDate, DateOnly lastDate, DateOnly? today)
    {
        var recorded = days.Where(x => x.AdherencePercent.HasValue).ToArray();
        if (recorded.Length == 0)
            return new(0, null, null, null, null);

        var weight = recorded.LastOrDefault(d => d.WeightKg.HasValue);
        var waist = recorded.LastOrDefault(d => d.WaistCm.HasValue);
        return new(
            recorded.Length,
            Math.Round(recorded.Average(d => d.AdherencePercent!.Value), 1, MidpointRounding.AwayFromZero),
            weight is null ? null : new(weight.Date, weight.WeightKg!.Value),
            waist is null ? null : new(waist.Date, waist.WaistCm!.Value),
            StreakAt(logs, firstDate, lastDate, today));
    }

    private static int? StreakAt(
        IReadOnlyDictionary<DateOnly, Year365DailyLog> logs, DateOnly firstDate, DateOnly lastDate, DateOnly? today)
    {
        if (!today.HasValue || today.Value < firstDate)
            return null;
        var cutoff = today.Value < lastDate ? today.Value : lastDate;
        var streak = 0;
        while (logs.TryGetValue(cutoff, out var log) && log.Adherence >= 0.60m)
        {
            streak++;
            if (log.Day == 1)
                break;
            cutoff = cutoff.AddDays(-1);
        }
        return streak;
    }

    private static DateOnly? ParseMonth(string? month)
    {
        if (month is null)
            return null;
        if (DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        throw InvalidMonth("El mes debe tener el formato AAAA-MM.");
    }

    private static ValidationException InvalidMonth(string message) =>
        new([new ValidationFailure("month", message)]);

    private static DateOnly FirstOfMonth(DateOnly value) => new(value.Year, value.Month, 1);

    private static string MonthKey(DateOnly value) => value.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
