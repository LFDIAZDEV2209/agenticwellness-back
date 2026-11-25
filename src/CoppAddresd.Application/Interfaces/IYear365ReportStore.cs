using CoppAddresd.Application.Features.Wellness.Year365;

namespace CoppAddresd.Application.Interfaces;

public interface IYear365ReportStore
{
    Task<Year365Program?> FindProgramAsync(Guid patientId, CancellationToken ct);
    Task<IReadOnlyList<Year365DailyLog>> ListDailyLogsAsync(Guid patientId, CancellationToken ct);
    Task<IReadOnlyList<Year365StoredEvent>> ListEventsAsync(Guid patientId, int firstDay, int lastDay, CancellationToken ct);
}
