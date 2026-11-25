using CoppAddresd.Domain.Wellness365;

namespace CoppAddresd.Application.Features.Motor365;

public sealed record MotorSubject(Guid Id, Guid? UserId, string Name, string Timezone, DateTime? BirthDate = null);
public sealed record MotorReceipt(Guid Id, string Version, DateTimeOffset AcceptedAt);
public sealed record MotorReceipts(MotorReceipt Terms, MotorReceipt Privacy);
public sealed record MotorData(Motor365Person? Person, Motor365Screening? Screening, bool Approved,
    MotorReceipts? Receipts, IReadOnlyList<Motor365DailyLog> Logs, int? ParamsVersion, DateTime? BirthDate = null);

/// <summary>Unidad de persistencia de perfiles, planes y revisiones del motor.</summary>
public interface IMotor365Store
{
    Task<T> InTransactionAsync<T>(bool write, Func<CancellationToken, Task<T>> operation, CancellationToken ct);
    Task<MotorSubject?> FindSubjectAsync(Guid patientId, CancellationToken ct);
    Task<MotorData> LoadAsync(Guid id, bool tracked, CancellationToken ct);
    Task<bool> HasSubmissionAsync(Guid id, Guid submission, CancellationToken ct);
    Task<MotorReceipts?> FindCurrentReceiptsAsync(Guid id, Guid userId, bool linked, string? termsVersion, CancellationToken ct);
    Task LinkReceiptsAsync(Guid id, MotorReceipts receipts, DateTimeOffset now, CancellationToken ct);
    Task<Motor365EngineParams?> FindParamsAsync(int? version, CancellationToken ct);
    Task<IReadOnlyList<MotorAuditEntry>> ReadAuditAsync(int page, int pageSize, CancellationToken ct);
    Task<bool> CanConnectAsync(CancellationToken ct);
    Task FlushAsync(CancellationToken ct);
    void AddPerson(Motor365Person person);
    void AddScreening(Motor365Screening screening);
    void AddLog(Motor365DailyLog log);
    void AddParams(Motor365EngineParams parameters);
    void AddSnapshot(Motor365PlanSnapshot snapshot);
    void AddEvents(IEnumerable<Motor365EngineEvent> events);
    void AddAudit(Motor365AuditLog entry);
}

/// <summary>Identidad y aplicación obtenidas del JWT validado.</summary>
public interface IMotor365Actor
{
    Guid? UserId { get; }
    bool IsApp { get; }
}
