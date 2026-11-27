using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.Wellness365;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed class ProtocolRewardsRepository(AppDbContext db, TimeProvider clock) : IProtocolRewards
{
    private static readonly IReadOnlyDictionary<string, string> StepRules = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["exercise"] = "PROTOCOL_EXERCISE", ["diet"] = "PROTOCOL_DIET",
        ["vitals"] = "PROTOCOL_VITALS", ["emotional"] = "PROTOCOL_EMOTIONAL", ["chest"] = "PROTOCOL_CHEST"
    };

    public async Task<ProtocolRewardsSnapshot> ReadAsync(Guid patientId, CancellationToken ct)
    {
        var (_, date) = await LocalDayAsync(patientId, ct);
        return await SnapshotAsync(patientId, date, ct);
    }

    public async Task<IReadOnlyList<LegacyRewardEntry>> ReadLegacyHistoryAsync(Guid id, int page, CancellationToken ct) =>
        await db.XpLedgerEntries.AsNoTracking().Where(x => x.Enrollment!.PatientId == id)
            .OrderByDescending(x => x.AwardedAt).ThenByDescending(x => x.Id).Skip((page - 1) * 50).Take(50)
            .Select(x => new LegacyRewardEntry(x.Id, x.Amount, x.BalanceAfter, x.AwardedAt, x.Reason.ToString())).ToListAsync(ct);

    public Task<ProtocolRewardsSnapshot> CompleteAsync(Guid patientId, DateOnly date, string step, CancellationToken ct)
    {
        if (!StepRules.TryGetValue(step, out var rule))
            throw new UnprocessableEntityException("PROTOCOL_STEP_NOT_SUPPORTED: el paso no pertenece al protocolo.");
        return WriteAsync(patientId, date, async (zone, day, planDay) =>
        {
            if (day.Steps.TryGetValue(step, out var existing)) return new(step, existing, false);
            if (day.Closed || (planDay != null && await db.Motor365DailyLogs.AnyAsync(l => l.PersonId == patientId && l.Day == planDay, ct)))
                throw new BusinessRuleViolationException("PROTOCOL_DAY_CLOSED: el día ya se cerró.");
            if (step == "chest" && day.Steps.Count != 4)
                throw new BusinessRuleViolationException("PROTOCOL_CHEST_LOCKED: faltan pasos por completar.");
            var amount = await ValueAsync(patientId, step, rule, date, zone, ct);
            var inserted = await InsertAsync(patientId, date, step, amount, planDay, ct);
            return new(step, amount, inserted == 1);
        }, ct);
    }

    public Task<ProtocolRewardsSnapshot> CloseAsync(Guid patientId, DateOnly date, CancellationToken ct) =>
        WriteAsync(patientId, date, async (zone, day, planDay) =>
        {
            if (planDay != null)
                throw new BusinessRuleViolationException("MOTOR_CLOSURE_REQUIRED: guarda el cierre del Motor 365.");
            await CloseAsync(patientId, date, zone, day, null, ct);
            return null;
        }, ct);

    public async Task CloseMotorDayAsync(Guid patientId, int planDay, DateOnly date, string timezone, CancellationToken ct)
    {
        await LockAsync(patientId, ct);
        // Un cierre offline se vincula a la fecha que el servidor registró al
        // otorgar los pasos, incluso si cambió la zona tras ese día del motor.
        var tracked = await db.Database.SqlQuery<DateOnly>($"""
            SELECT DISTINCT local_date AS "Value" FROM app.protocol_reward_entries
            WHERE patient_id = {patientId} AND plan_day = {planDay} AND local_date <= {date}
            """).ToListAsync(ct);
        foreach (var localDate in tracked.Count > 0 ? tracked : [date])
        {
            var day = ToDay(localDate, await EntriesAsync(patientId, localDate, ct));
            await CloseAsync(patientId, localDate, FindZone(timezone), day, planDay, ct);
        }
    }

    private async Task CloseAsync(Guid id, DateOnly date, TimeZoneInfo zone, ProtocolRewardDay day, int? planDay, CancellationToken ct)
    {
        if (day.Closed) return;
        var bonus = day.Steps.ContainsKey("chest") ? await ValueAsync(id, "close", "PROTOCOL_SEQUENCE", date, zone, ct) : 0;
        await InsertAsync(id, date, "close", bonus, planDay, ct);
    }

    private Task<ProtocolRewardsSnapshot> WriteAsync(Guid id, DateOnly expectedDate,
        Func<TimeZoneInfo, ProtocolRewardDay, int?, Task<ProtocolRewardReceipt?>> operation, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockAsync(id, ct);
            var (zone, date) = await LocalDayAsync(id, ct);
            if (expectedDate != date)
                throw new BusinessRuleViolationException("PROTOCOL_STALE_DAY: actualiza el protocolo del día local.");
            var motor = await db.Motor365People.AsNoTracking().SingleOrDefaultAsync(x => x.PatientId == id, ct);
            int? planDay = motor is null ? null : Math.Clamp((motor.CalendarAnchorDay ?? 1) + date.DayNumber
                - (motor.CalendarAnchorDate ?? motor.StartDate).DayNumber, 1, 365);
            var receipt = await operation(zone, ToDay(date, await EntriesAsync(id, date, ct)), planDay);
            var snapshot = (await SnapshotAsync(id, date, ct)) with { Receipt = receipt };
            await tx.CommitAsync(ct);
            return snapshot;
        });

    private Task<Guid> LockAsync(Guid id, CancellationToken ct) => db.Database.SqlQuery<Guid>($"""
        SELECT id AS "Value" FROM app.patient_profiles WHERE id = {id} FOR UPDATE
        """).SingleAsync(ct);

    private async Task<(TimeZoneInfo Zone, DateOnly Date)> LocalDayAsync(Guid id, CancellationToken ct)
    {
        var timezone = await db.PatientProfiles.AsNoTracking().Where(x => x.Id == id && x.DeletedAt == null)
            .Select(x => x.Timezone).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Perfil no encontrado.");
        var zone = FindZone(timezone);
        return (zone, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime));
    }

    private static TimeZoneInfo FindZone(string zone) => TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var found)
        ? found : throw new BusinessRuleViolationException("PROTOCOL_INVALID_TIMEZONE: falta una zona IANA válida.");

    private async Task<int> ValueAsync(Guid id, string step, string code, DateOnly date, TimeZoneInfo zone, CancellationToken ct)
    {
        var rule = await XpRuleTimeline.ResolveAsync(db, code, date, zone, ct);
        if (rule is not { BaseXp: int amount, RequiresValidation: false } || rule.MaxPerDay == 0) return 0;
        if (rule.MaxPerWeek is { } max)
        {
            var monday = date.AddDays(-((int)date.DayOfWeek + 6) % 7);
            var sunday = monday.AddDays(6);
            var granted = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM app.protocol_reward_entries
                WHERE patient_id = {id} AND step = {step} AND amount > 0 AND local_date BETWEEN {monday} AND {sunday}
                """).SingleAsync(ct);
            if (granted >= max) return 0;
        }
        return (int)Math.Floor(amount * rule.Multiplier);
    }

    private Task<int> InsertAsync(Guid id, DateOnly date, string step, int amount, int? planDay, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.protocol_reward_entries (patient_id, local_date, step, amount, awarded_at, plan_day)
            VALUES ({id}, {date}, {step}, {amount}, {clock.GetUtcNow()}, {planDay})
            ON CONFLICT (patient_id, local_date, step) DO NOTHING
            """, ct);

    private sealed record Entry(DateOnly LocalDate, string Step, int Amount);

    private Task<List<Entry>> EntriesAsync(Guid id, DateOnly? date, CancellationToken ct) => date is { } day
        ? db.Database.SqlQuery<Entry>($"""
            SELECT local_date AS "LocalDate", step AS "Step", amount AS "Amount"
            FROM app.protocol_reward_entries WHERE patient_id = {id} AND local_date = {day}
            """).ToListAsync(ct)
        : db.Database.SqlQuery<Entry>($"""
            SELECT local_date AS "LocalDate", step AS "Step", amount AS "Amount"
            FROM app.protocol_reward_entries WHERE patient_id = {id}
            """).ToListAsync(ct);

    private static ProtocolRewardDay ToDay(DateOnly date, IEnumerable<Entry> entries)
    {
        var steps = entries.ToDictionary(e => e.Step, e => e.Amount, StringComparer.Ordinal);
        var closed = steps.Remove("close", out var bonus);
        return new(date, steps, bonus, closed);
    }

    private async Task<ProtocolRewardsSnapshot> SnapshotAsync(Guid id, DateOnly date, CancellationToken ct)
    {
        // La migración es aditiva: los asientos legados siguen en su libro
        // mayor y se proyectan por paciente, sin reescritura ni duplicación.
        var legacy = await db.XpLedgerEntries.AsNoTracking().Where(x => x.Enrollment!.PatientId == id)
            .MaxAsync(x => (int?)x.BalanceAfter, ct) ?? 0;
        var previous = await db.StreakStates.AsNoTracking().Where(x => x.Enrollment!.PatientId == id)
            .OrderByDescending(x => x.LastActiveDate != null).ThenByDescending(x => x.LastActiveDate)
            .Select(x => new { x.CurrentStreak, x.LastActiveDate }).FirstOrDefaultAsync(ct);
        var historicalLongest = await db.StreakStates.AsNoTracking().Where(x => x.Enrollment!.PatientId == id)
            .MaxAsync(x => (int?)x.LongestStreak, ct) ?? 0;
        var rows = await EntriesAsync(id, null, ct);
        var days = rows.GroupBy(row => row.LocalDate).Select(g => ToDay(g.Key, g))
            .OrderByDescending(day => day.LocalDate).ToArray();
        var today = days.FirstOrDefault(day => day.LocalDate == date) ?? ToDay(date, []);
        var start = today.Steps.ContainsKey("chest") ? date : date.AddDays(-1);
        var completed = days.Where(d => d.LocalDate <= date && d.Steps.ContainsKey("chest")).Select(d => d.LocalDate).ToHashSet();
        var streak = 0;
        while (completed.Contains(start)) { streak++; start = start.AddDays(-1); }
        if (previous is { LastActiveDate: { } previousDate } && previousDate == start) streak += previous.CurrentStreak;
        var run = 0;
        var longest = Math.Max(streak, historicalLongest);
        DateOnly? prior = null;
        foreach (var day in completed.Order())
        {
            var yesterday = day.AddDays(-1);
            run = prior == yesterday ? run + 1
                : previous?.LastActiveDate == yesterday ? previous.CurrentStreak + 1 : 1;
            longest = Math.Max(longest, run);
            prior = day;
        }
        var balance = legacy + rows.Sum(r => r.Amount);
        var (level, levelName, next) = XpLevels.ForProtocolBalance(balance);
        return new(today, balance, legacy, level, levelName, next, streak, longest, days.Take(30).ToArray());
    }
}
