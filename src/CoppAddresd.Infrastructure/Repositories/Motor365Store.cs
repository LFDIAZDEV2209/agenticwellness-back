using System.Data;
using CoppAddresd.Application.Features.Motor365;
using CoppAddresd.Application.Features.LegalDocuments;
using CoppAddresd.Domain.Wellness365;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Options;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>Persistencia EF del contrato Motor 365 sobre el esquema existente.</summary>
public sealed class Motor365Store(AppDbContext db, IOptionsMonitor<AccountLegalConsentSettings>? legal = null) : IMotor365Store
{
    public Task<T> InTransactionAsync<T>(bool write, Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(
                    write ? IsolationLevel.Serializable : IsolationLevel.RepeatableRead, token);
                var result = await operation(token);
                if (write) await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return result;
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new MotorException("version_conflict");
            }
            catch (Exception ex) when (ex.GetBaseException() is PostgresException
                { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DeadlockDetected })
            {
                throw new MotorException("version_conflict");
            }
            catch (Exception ex) when (ex is NpgsqlException { IsTransient: true }
                || ex.InnerException is NpgsqlException { IsTransient: true })
            {
                // El commit puede haber llegado a la BD. La reejecución corresponde al cliente.
                throw new MotorException("service_unavailable");
            }
        }, ct);

    public Task<MotorSubject?> FindSubjectAsync(Guid patientId, CancellationToken ct) =>
        db.PatientProfiles.AsNoTracking().Where(p => p.Id == patientId && p.DeletedAt == null && p.Status == "Activo")
            .Select(p => new MotorSubject(p.Id, p.UserId, p.FirstName + " " + p.LastName, p.Timezone, p.DateOfBirth))
            .SingleOrDefaultAsync(ct);

    public async Task<MotorData> LoadAsync(Guid id, bool tracked, CancellationToken ct)
    {
        var person = await Tracking(db.Motor365People, tracked).SingleOrDefaultAsync(p => p.PatientId == id, ct);
        if (person is null) return new(null, null, false, null, [], null);
        var screening = await db.Motor365Screenings.AsNoTracking().Where(x => x.PersonId == id)
            .OrderByDescending(x => x.RecordedAt).ThenByDescending(x => x.SubmissionId).FirstOrDefaultAsync(ct);
        var approved = screening is not null && await db.Motor365ProfessionalReviews.AsNoTracking()
            .Where(x => x.PersonId == id && x.SubmissionId == screening.SubmissionId)
            .OrderByDescending(x => x.ReviewedAt).ThenByDescending(x => x.Id)
            .Select(x => (Motor365ReviewDecision?)x.Decision).FirstOrDefaultAsync(ct) == Motor365ReviewDecision.Approved;
        var owner = await db.PatientProfiles.AsNoTracking().Where(x => x.Id == id).Select(x => x.UserId).SingleAsync(ct);
        var receipts = owner.HasValue ? await FindCurrentReceiptsAsync(id, owner.Value, true, null, ct) : null;
        var logs = await Tracking(db.Motor365DailyLogs, tracked).Where(x => x.PersonId == id).OrderBy(x => x.Day).ToListAsync(ct);
        var version = await db.Motor365PlanSnapshots.AsNoTracking().Where(x => x.PersonId == id && x.PlanVersion == person.PlanVersion)
            .OrderByDescending(x => x.ParamsVersion).Select(x => (int?)x.ParamsVersion).FirstOrDefaultAsync(ct);
        return new(person, screening, approved, receipts, logs, version);

        static IQueryable<T> Tracking<T>(DbSet<T> rows, bool enabled) where T : class => enabled ? rows : rows.AsNoTracking();
    }

    public Task<bool> HasSubmissionAsync(Guid id, Guid submission, CancellationToken ct) =>
        db.Motor365Screenings.AnyAsync(x => x.PersonId == id && x.SubmissionId == submission, ct);

    public async Task<MotorReceipts?> FindCurrentReceiptsAsync(Guid id, Guid userId, bool linked, string? termsVersion, CancellationToken ct)
    {
        var config = legal?.CurrentValue;
        var pairs = new[] { "es", "en" }.Select(locale =>
        {
            var codes = config is { EnforcementEnabled: true } || config?.Documents.Count > 0
                ? config?.Documents.GetValueOrDefault(locale)
                : new AccountLegalDocumentCodes { Terms = "paid-demo-terms-" + locale, Privacy = "paid-demo-privacy-" + locale };
            return (Locale: locale, Codes: codes);
        }).Where(pair => pair.Codes is not null).ToDictionary(pair => pair.Locale, pair => pair.Codes!);
        var documentCodes = pairs.Values.SelectMany(pair => new[] { pair.Terms, pair.Privacy }).Distinct().ToArray();
        if (documentCodes.Length == 0) return null;

        var candidates = await (
            from acceptance in db.LegalDocumentAcceptances.AsNoTracking()
            join version in db.LegalDocumentVersions.AsNoTracking() on acceptance.LegalDocumentVersionId equals version.Id
            join document in db.LegalDocuments.AsNoTracking() on version.DocumentId equals document.Id
            where acceptance.UserId == userId && acceptance.WithdrawnAt == null && version.IsPublished &&
                document.CurrentVersionId == version.Id && documentCodes.Contains(document.Code) &&
                (!linked || db.Motor365Consents.Any(x => x.PersonId == id && x.AcceptanceId == acceptance.Id))
            orderby acceptance.AcceptedAt descending, acceptance.Id descending
            select new { acceptance.Id, acceptance.Locale, acceptance.Scope, acceptance.AcceptedAt, document.Code, version.Major, version.Minor })
            .ToListAsync(ct);

        foreach (var term in candidates)
        {
            if (!pairs.TryGetValue(term.Locale, out var pair) || term.Code != pair.Terms ||
                !IsScope(term.Scope, term.Code, term.Locale, AccountLegalScopes.Terms, "terms")) continue;
            var version = $"{term.Major}.{term.Minor}";
            if (!string.IsNullOrWhiteSpace(termsVersion) && termsVersion != version) continue;
            var privacy = candidates.FirstOrDefault(x => x.Locale == term.Locale && x.Code == pair.Privacy &&
                IsScope(x.Scope, x.Code, x.Locale, AccountLegalScopes.Privacy, "privacy"));
            if (privacy is null) continue;
            return new(new(term.Id, version, Utc(term.AcceptedAt)),
                new(privacy.Id, $"{privacy.Major}.{privacy.Minor}", Utc(privacy.AcceptedAt)));
        }
        return null;

        static bool IsScope(string scope, string code, string locale, string accountScope, string demoKind) =>
            scope == accountScope || (scope == code && code == $"paid-demo-{demoKind}-{locale}");

        static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    public async Task LinkReceiptsAsync(Guid id, MotorReceipts receipts, DateTimeOffset now, CancellationToken ct)
    {
        Guid[] ids = [receipts.Terms.Id, receipts.Privacy.Id];
        var existing = await db.Motor365Consents.Where(x => x.PersonId == id && ids.Contains(x.AcceptanceId))
            .Select(x => x.AcceptanceId).ToListAsync(ct);
        db.Motor365Consents.AddRange(ids.Except(existing)
            .Select(receipt => new Motor365Consent { PersonId = id, AcceptanceId = receipt, RecordedAt = now }));
    }

    public Task<Motor365EngineParams?> FindParamsAsync(int? version, CancellationToken ct) =>
        db.Motor365EngineParams.AsNoTracking().Where(x => !version.HasValue || x.Version == version)
            .OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<MotorAuditEntry>> ReadAuditAsync(int page, int pageSize, CancellationToken ct)
    {
        var people = db.Motor365AuditLog.AsNoTracking()
            .Select(x => new { At = x.OccurredAt, Who = x.ActorUserId, x.Action, Ref = x.PersonId.ToString() });
        var parameters = db.Motor365EngineParams.AsNoTracking().Where(x => x.Version > 1)
            .Select(x => new { At = x.CreatedAt, Who = (Guid?)x.ChangedBy, Action = "params.update", Ref = x.Version.ToString() });
        return await people.Concat(parameters).OrderByDescending(x => x.At)
            .ThenBy(x => x.Ref).ThenBy(x => x.Action).ThenBy(x => x.Who)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new MotorAuditEntry(x.At, x.Who, x.Action, x.Ref)).ToListAsync(ct);
    }

    public Task<bool> CanConnectAsync(CancellationToken ct) => db.Database.CanConnectAsync(ct);
    public Task FlushAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
    public void AddPerson(Motor365Person person) => db.Motor365People.Add(person);
    public void AddScreening(Motor365Screening screening) => db.Motor365Screenings.Add(screening);
    public void AddLog(Motor365DailyLog log) => db.Motor365DailyLogs.Add(log);
    public void AddParams(Motor365EngineParams parameters) => db.Motor365EngineParams.Add(parameters);
    public void AddSnapshot(Motor365PlanSnapshot snapshot) => db.Motor365PlanSnapshots.Add(snapshot);
    public void AddEvents(IEnumerable<Motor365EngineEvent> events) => db.Motor365EngineEvents.AddRange(events);
    public void AddAudit(Motor365AuditLog entry) => db.Motor365AuditLog.Add(entry);
}
