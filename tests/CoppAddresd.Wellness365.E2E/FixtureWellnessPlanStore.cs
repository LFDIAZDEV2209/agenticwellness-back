using CoppAddresd.Application.Features.Wellness365;
using CoppAddresd.Domain.Wellness365;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;



/// <summary>Persiste escenarios aislados del calendario sin exigir alta y recibos.</summary>
sealed class FixtureWellnessPlanStore(AppDbContext db, WellnessPlanService plans)
{
    public async Task<WellnessPlanPerson?> ReadAsync(Guid patientId, WellnessBlock? eligibilityBlock,
        CancellationToken ct)
    {
        var row = await (from person in db.Motor365People.AsNoTracking()
                         join patient in db.PatientProfiles.AsNoTracking() on person.PatientId equals patient.Id
                         where person.PatientId == patientId && patient.DeletedAt == null
                         select new
                         {
                             Header = new PlanHeader(person, patient.Timezone, patient.DateOfBirth),
                             Logs = db.Motor365DailyLogs.AsNoTracking().Where(x => x.PersonId == person.PatientId).ToArray()
                         }).AsSingleQuery().SingleOrDefaultAsync(ct);
        if (row is null) return null;

        var logs = row.Logs.ToDictionary(x => x.Day, x => new WellnessDailyLog((double)x.Adherence,
            (double)x.SleepHours, x.Energy, x.Pain, (double?)x.WeightKg, x.CompletedExercise)
            { Symptoms = x.Symptoms });
        return row.Header.ToPlan(eligibilityBlock, logs);
    }

    public Task<WellnessPlanResult<WellnessPlanClosure>> CloseAsync(Guid patientId, Guid actorUserId,
        string? day, WellnessLogInput input, CancellationToken ct) =>
        WithPlanLockAsync(patientId, async (header, token) =>
        {
            var result = plans.ValidateClosure(header?.ToPlan(block: null), day, input);
            if (result.Value is not { } close || header is null) return result;

            var symptoms = input.Symptoms.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO app.daily_log AS logged
                     (person_id, day, log_date, adherence, sleep_hours, energy, pain, symptoms, weight_kg, waist_cm, completed_exercise)
                VALUES ({patientId}, {close.Day}, {close.LogDate}, {input.Adherence}, {input.SleepHours},
                        {input.Energy}, {input.Pain}, {symptoms}, {input.WeightKg}, {input.WaistCm}, {input.CompletedExercise})
                ON CONFLICT (person_id, day) DO UPDATE
                SET adherence = EXCLUDED.adherence, sleep_hours = EXCLUDED.sleep_hours, energy = EXCLUDED.energy,
                    pain = EXCLUDED.pain, symptoms = EXCLUDED.symptoms, weight_kg = EXCLUDED.weight_kg,
                    waist_cm = EXCLUDED.waist_cm, completed_exercise = EXCLUDED.completed_exercise
                WHERE (logged.adherence, logged.sleep_hours, logged.energy, logged.pain, logged.symptoms,
                       logged.weight_kg, logged.waist_cm, logged.completed_exercise) IS DISTINCT FROM
                      (EXCLUDED.adherence, EXCLUDED.sleep_hours, EXCLUDED.energy, EXCLUDED.pain, EXCLUDED.symptoms,
                       EXCLUDED.weight_kg, EXCLUDED.waist_cm, EXCLUDED.completed_exercise)
                """, token);

            if (changed > 0)
            {
                await db.Motor365People.Where(x => x.PatientId == patientId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.PlanVersion, x => x.PlanVersion + 1), token);
                await AuditAsync(patientId, actorUserId, "daily_log_upsert", token);
            }

            return result;
        }, ct);

    public Task<WellnessPlanCalendar?> ChangeTimeZoneAsync(Guid patientId, Guid actorUserId,
        string timeZoneId, CancellationToken ct) =>
        WithPlanLockAsync<WellnessPlanCalendar?>(patientId, async (header, token) =>
        {
            if (header is null) return null;
            var original = header.Calendar;
            var calendar = plans.ChangeTimeZone(original, timeZoneId);
            if (calendar == original) return original;

            await db.Motor365People.Where(x => x.PatientId == patientId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.CalendarTimeZoneId, calendar.TimeZoneId)
                    .SetProperty(x => x.CalendarAnchorDate, calendar.AnchorLocalDate)
                    .SetProperty(x => x.CalendarAnchorDay, calendar.AnchorDay)
                    .SetProperty(x => x.PlanVersion, x => x.PlanVersion + 1), token);
            await AuditAsync(patientId, actorUserId, "calendar_zone_change", token);
            return calendar;
        }, ct);

    private Task<T> WithPlanLockAsync<T>(Guid patientId, Func<PlanHeader?, CancellationToken, Task<T>> operation,
        CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT patient_id FROM app.person WHERE patient_id = {patientId} FOR UPDATE", token);
            var header = await (from person in db.Motor365People.AsNoTracking()
                                join patient in db.PatientProfiles.AsNoTracking() on person.PatientId equals patient.Id
                                where person.PatientId == patientId && patient.DeletedAt == null
                                select new PlanHeader(person, patient.Timezone, patient.DateOfBirth))
                .SingleOrDefaultAsync(token);
            var result = await operation(header, token);
            await transaction.CommitAsync(token);
            return result;
        }, ct);

    private Task<int> AuditAsync(Guid patientId, Guid actorUserId, string action, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.audit_log (person_id, actor_user_id, action)
            VALUES ({patientId}, {actorUserId}, {action})
            """, ct);

    private sealed record PlanHeader(Motor365Person Person, string ProfileTimeZone, DateTime? BirthDate)
    {
        public WellnessPlanCalendar Calendar => new(Person.StartDate, Person.CalendarTimeZoneId ?? ProfileTimeZone,
            Person.CalendarAnchorDate ?? Person.StartDate, Person.CalendarAnchorDay ?? 1);

        public WellnessPlanPerson ToPlan(WellnessBlock? block, IReadOnlyDictionary<int, WellnessDailyLog>? logs = null)
        {
            var age = Person.StartDate.Year - (BirthDate?.Year ?? Person.BirthYear);
            // Sin el día/mes canónico se toma la edad mínima posible durante el año.
            if (BirthDate is null || Person.StartDate < DateOnly.FromDateTime(BirthDate.Value).AddYears(age)) age--;

            var profile = new WellnessProfile(Person.Sex, age, (double)Person.HeightCm,
                (double)Person.InitialWeightKg, (double)Person.InitialWaistCm, (double?)Person.TargetWeightKg,
                Person.ActivityLevel, Person.Goal, 1, 0)
            {
                Pace = Person.Pace, Vegetarian = Person.Vegetarian, LactoseFree = Person.LactoseFree,
                FitnessLevel = Person.FitnessLevel
            };
            if (logs is not null) profile = profile with { Logs = logs };
            return new(profile, Calendar, block);
        }
    }
}
