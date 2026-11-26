namespace CoppAddresd.Application.Features.Wellness365;

/// <summary>Calendario persistible. El inicio original y las claves de registros no se reescriben.</summary>
public sealed record WellnessPlanCalendar(DateOnly StartDate, string TimeZoneId,
    DateOnly AnchorLocalDate, int AnchorDay);
