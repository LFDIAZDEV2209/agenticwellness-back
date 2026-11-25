using System.Globalization;
using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.Application.Features.Wellness365;

public sealed class WellnessPlanService
{
    private readonly TimeProvider _clock;
    private readonly bool _demo;

    public WellnessPlanService(TimeProvider clock) : this(clock, demo: false) { }

    private WellnessPlanService(TimeProvider clock, bool demo) => (_clock, _demo) = (clock, demo);

    // Solo para un host de prueba separado. El contrato de producción no puede elegir este modo.
    public static WellnessPlanService CreateDemo(TimeProvider clock) => new(clock, demo: true);

    public WellnessPlanCalendar Activate(string timeZoneId, DateOnly? requestedStartDate = null)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var today = LocalDate(_clock.GetUtcNow(), zone);
        var start = _demo ? requestedStartDate ?? today : today;
        return new(start, zone.Id, start, 1);
    }

    public WellnessPlanCalendar ChangeTimeZone(WellnessPlanCalendar calendar, string timeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        if (zone.Id == calendar.TimeZoneId) return calendar;

        // Una sola lectura del reloj para que ambos lados del cambio representen el mismo instante.
        var now = _clock.GetUtcNow();
        return calendar with
        {
            TimeZoneId = zone.Id,
            AnchorLocalDate = LocalDate(now, zone),
            AnchorDay = Today(calendar, now)
        };
    }

    public WellnessPlanResult<WellnessPlanDayView> GetDay(WellnessPlanPerson? person, string? requestedDay = null)
    {
        if (person is null) return Failed<WellnessPlanDayView>(WellnessPlanErrorCode.PersonNotFound);
        var day = 0;
        if (requestedDay is not null && !TryDay(requestedDay, out day))
            return Failed<WellnessPlanDayView>(WellnessPlanErrorCode.BadDay);

        var block = FindBlock(person);
        if (block is not null) return Failed<WellnessPlanDayView>(WellnessPlanErrorCode.PlanLocked, block);
        var calendar = person.Calendar!;
        var localDate = LocalDate(_clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId));
        var today = Today(calendar, localDate);
        if (requestedDay is null) day = today;
        if (day > today) return Failed<WellnessPlanDayView>(WellnessPlanErrorCode.FutureDay);

        var profile = person.Profile with { CurrentDay = today, Real = !_demo };
        var timeline = _demo ? WellnessEngine.Simulate(profile) : WellnessEngine.EvaluateRecordedTimeline(profile);
        var recommendation = timeline.Days[day - 1];
        var plan = WellnessEngine.PlanDay(profile, recommendation);
        var status = new WellnessPlanStatus(recommendation.Paused ? "paused"
            : recommendation.Mode == WellnessMode.Maintain ? "maint" : "active");

        var recorded = profile.Logs.ContainsKey(day);
        var logStatus = recorded ? "recorded" : day == today ? "open" : _demo ? "simulated" : "missing";
        var showMeasurement = recorded || (_demo && day < today);
        var streak = recommendation.Streak ?? (day > 1 ? timeline.Days[day - 2].Streak : null) ?? 0;

        return new(new WellnessPlanDayView(day, today, localDate, calendar.StartDate, calendar.TimeZoneId, !_demo,
            status, logStatus, logStatus == "open" ? null : recommendation.Log.Adherence,
            showMeasurement ? recommendation.Log.MeasuredWeightKg : null, streak,
            recommendation.Paused ? null : recommendation.Exercise, plan.Menu, recommendation.Reasons,
            plan.EngineVersion, plan.ParametersVersion));
    }

    // Los registros no dependen de que se pueda leer el plan, pero necesitan un calendario activo.
    public WellnessPlanResult<WellnessPlanClosure> ValidateClosure(WellnessPlanPerson? person, string? requestedDay)
    {
        if (person is null) return Failed<WellnessPlanClosure>(WellnessPlanErrorCode.PersonNotFound);
        if (!TryDay(requestedDay, out var day)) return Failed<WellnessPlanClosure>(WellnessPlanErrorCode.BadDay);
        if (person.Calendar is not { } calendar)
            return Failed<WellnessPlanClosure>(WellnessPlanErrorCode.PlanLocked, FindBlock(person));
        if (day > Today(calendar, _clock.GetUtcNow()))
            return Failed<WellnessPlanClosure>(WellnessPlanErrorCode.FutureDay);
        // log_date es la fecha nominal del ordinal del plan; una nueva zona solo redefine
        // cuándo se avanza de ordinal y nunca hace colisionar dos cierres.
        return new(new WellnessPlanClosure(day, calendar.StartDate.AddDays(day - 1)));
    }

    public WellnessPlanResult<WellnessPlanClosure> ValidateClosure(WellnessPlanPerson? person,
        string? requestedDay, WellnessLogInput input)
    {
        var result = ValidateClosure(person, requestedDay);
        return result.Error is null && !input.IsValid()
            ? Failed<WellnessPlanClosure>(WellnessPlanErrorCode.BadLog)
            : result;
    }

    private static WellnessBlock? FindBlock(WellnessPlanPerson person) =>
        person.EligibilityBlock ?? WellnessEngine.CheckBlock(person.Profile) ??
        (person.Calendar is null
            ? new("not_started", new("El plan aún no está activo.", "The plan is not active yet."))
            : null);

    private static bool TryDay(string? value, out int day) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out day) &&
        day is >= 1 and <= WellnessEngine.PlanLength;

    private static int Today(WellnessPlanCalendar calendar, DateTimeOffset now) =>
        Today(calendar, LocalDate(now, TimeZoneInfo.FindSystemTimeZoneById(calendar.TimeZoneId)));

    private static int Today(WellnessPlanCalendar calendar, DateOnly local) =>
        Math.Clamp(calendar.AnchorDay + local.DayNumber - calendar.AnchorLocalDate.DayNumber,
            1, WellnessEngine.PlanLength);

    private static DateOnly LocalDate(DateTimeOffset now, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

    private static WellnessPlanResult<T> Failed<T>(WellnessPlanErrorCode code, WellnessBlock? block = null)
        where T : class => new(new WellnessPlanError(code,
            block is null ? null : new("locked", block.Code, block.Message)));
}
