using System.Buffers.Binary;
using System.Text.Json;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Features.Wellness365;
using CoppAddresd.Domain.Wellness365;
using MediatR;

namespace CoppAddresd.Application.Features.Motor365;

public sealed record WriteMotorPerson(Guid? Id, MotorPersonInput Input) : IRequest<MotorPersonWrite>;
public sealed record UpdateMotorPerson(Guid Id, MotorPersonInput Input) : IRequest<MotorPersonResult>;
public sealed record GetMotorPerson(Guid Id) : IRequest<MotorPersonDto>;
public sealed record GetMotorPlan(Guid Id) : IRequest<MotorPlanDto>;
public sealed record GetMotorDay(Guid Id, int Day) : IRequest<MotorDayDto>;
public sealed record GetMotorEvents(Guid Id) : IRequest<IReadOnlyList<MotorEventDto>>;
public sealed record PutMotorLog(Guid Id, int Day, MotorLogInput Input) : IRequest<MotorLogResult>;

public sealed class WriteMotorPersonHandler(MotorPeople people) : IRequestHandler<WriteMotorPerson, MotorPersonWrite>
{
    public Task<MotorPersonWrite> Handle(WriteMotorPerson request, CancellationToken ct) => people.WriteAsync(request, ct);
}

public sealed class UpdateMotorPersonHandler(MotorPeople people) : IRequestHandler<UpdateMotorPerson, MotorPersonResult>
{
    public async Task<MotorPersonResult> Handle(UpdateMotorPerson request, CancellationToken ct) =>
        (await people.WriteAsync(new(request.Id, request.Input), ct)).Result;
}

public sealed class GetMotorPersonHandler(MotorPeople people) : IRequestHandler<GetMotorPerson, MotorPersonDto>
{
    public Task<MotorPersonDto> Handle(GetMotorPerson request, CancellationToken ct) => people.ReadAsync(request.Id, ct);
}

public sealed class GetMotorPlanHandler(MotorPeople people) : IRequestHandler<GetMotorPlan, MotorPlanDto>
{
    public Task<MotorPlanDto> Handle(GetMotorPlan request, CancellationToken ct) =>
        people.CalculateAsync(request.Id, (view, calculation) => new MotorPlanDto(request.Id, view.Day, view.Person.Ver,
            calculation.Result.Days.Select(d => MotorProjection.Day(d, calculation.Version)).ToArray(),
            WellnessEngine.ReferenceVersion, calculation.Version), ct);
}

public sealed class GetMotorDayHandler(MotorPeople people) : IRequestHandler<GetMotorDay, MotorDayDto>
{
    public Task<MotorDayDto> Handle(GetMotorDay request, CancellationToken ct) =>
        people.CalculateAsync(request.Id, (_, calculation) => MotorProjection.WithMenu(
            WellnessEngine.PlanDay(calculation.Profile, calculation.Result.Days[request.Day - 1]), calculation.Version), ct, request.Day);
}

public sealed class GetMotorEventsHandler(MotorPeople people) : IRequestHandler<GetMotorEvents, IReadOnlyList<MotorEventDto>>
{
    public Task<IReadOnlyList<MotorEventDto>> Handle(GetMotorEvents request, CancellationToken ct) =>
        people.CalculateAsync<IReadOnlyList<MotorEventDto>>(request.Id, (_, calculation) =>
            calculation.Result.Events.Select(e => MotorProjection.Event(e, calculation.Version)).ToArray(), ct);
}

public sealed class PutMotorLogHandler(MotorPeople people) : IRequestHandler<PutMotorLog, MotorLogResult>
{
    public Task<MotorLogResult> Handle(PutMotorLog request, CancellationToken ct) => people.CloseDayAsync(request, ct);
}

internal sealed record MotorView(MotorPersonDto Person, int Day);
internal sealed record MotorCalculation(WellnessProfile Profile, WellnessSimulation Result, int Version);

/// <summary>Orquesta el perfil propio o asignado y el cálculo versionado del motor.</summary>
public sealed class MotorPeople(IMotor365Store store, IMotor365Actor actor, IPatientScopeGuard scope, TimeProvider clock)
{
    public Task<MotorPersonWrite> WriteAsync(WriteMotorPerson request, CancellationToken ct) =>
        store.InTransactionAsync<MotorPersonWrite>(true, async token =>
        {
            var input = request.Input;
            var subject = await SubjectAsync(request.Id ?? input.Id, write: true, token);
            var data = (await store.LoadAsync(subject.Id, tracked: true, token)) with { BirthDate = subject.BirthDate };
            var now = clock.GetUtcNow();
            var local = LocalDate(subject, now);
            var created = data.Person is null;
            if (created && request.Id.HasValue) throw new MotorException("person_not_found");
            var person = data.Person ?? new Motor365Person { PatientId = subject.Id, StartDate = local };
            SyncCalendar(subject, person, now);
            var previous = created ? null : ToPerson(subject, person, data, local);
            var receipt = data.Receipts;
            if (input.Terms is not null && subject.UserId is { } owner)
            {
                receipt = await store.FindCurrentReceiptsAsync(subject.Id, owner, linked: false, input.Terms.Version, token);
                if (receipt is null) throw Gate("terms");
            }

            ApplyProfile(person, input);
            if (subject.BirthDate is { } birth && birth.Year <= person.StartDate.Year) person.BirthYear = birth.Year;
            if (person.BirthYear < 1900)
                throw new MotorException("invalid_request", issues: [new("invalid_field", "age")]);
            var profileChanged = previous is not null && ProfileChanged(person, previous, data.BirthDate);
            var screening = data.Screening;
            var parq = input.ParqA ?? ReadParq(screening) ?? new short?[7];
            var nqs = input.NqA ?? ReadNqs(screening) ?? new short?[6];
            var answersChanged = screening is null || !parq.SequenceEqual(ReadParq(screening)!) || !nqs.SequenceEqual(ReadNqs(screening)!);
            var newSubmission = input.SubmissionId is { } submission && submission != screening?.SubmissionId;
            var replacesScreening = (answersChanged && (input.ParqA is not null || input.NqA is not null || newSubmission))
                || newSubmission || (profileChanged && screening is not null);
            var logbook = data.Logs.ToDictionary(log => log.Day);
            var day = Day(person, local);
            int? firstChangedDay = null;
            foreach (var (number, log) in input.Log ?? [])
            {
                if (ApplyLog(person, day, logbook, number, log))
                    firstChangedDay = Math.Min(firstChangedDay ?? number, number);
            }

            var changed = created || replacesScreening || firstChangedDay.HasValue || receipt != data.Receipts ||
                profileChanged;

            if (input.SubmissionId is { } key && await store.HasSubmissionAsync(subject.Id, key, token))
            {
                if (key != screening?.SubmissionId || changed) throw new MotorException("submission_conflict");
                return new(new(subject.Id, previous!.Status, key, person.PlanVersion), false);
            }
            if (!changed)
                return new(new(subject.Id, previous!.Status, screening?.SubmissionId, person.PlanVersion), false);
            if (input.Ver.HasValue && (created || input.Ver != person.PlanVersion))
                throw new MotorException("version_conflict");
            if (!created) person.PlanVersion++;
            else store.AddPerson(person);

            var approved = data.Approved;
            if (replacesScreening)
            {
                var stamp = screening is not null && now <= screening.RecordedAt ? screening.RecordedAt.AddTicks(10) : now;
                screening = NewScreening(person.PatientId, input.SubmissionId ?? Guid.NewGuid(), stamp, parq, nqs);
                store.AddScreening(screening);
                approved = false;
            }
            if (receipt is not null && receipt != data.Receipts)
                await store.LinkReceiptsAsync(subject.Id, receipt, now, token);
            data = new(person, screening, approved, receipt, logbook.Values.ToArray(), null, data.BirthDate);
            var status = Status(person, data, day);
            if (status.Ok)
            {
                // La FK de completitud del snapshot se aplica en PostgreSQL.
                if (replacesScreening) await store.FlushAsync(token);
                await PublishAsync(person, data, day, firstChangedDay ?? day, now, token);
            }
            Audit(subject.Id, created ? "person.create" : request.Id.HasValue ? "person.update" : "person.upsert", now);
            return new(new(subject.Id, status, screening?.SubmissionId, person.PlanVersion), created);
        }, ct);

    public Task<MotorPersonDto> ReadAsync(Guid id, CancellationToken ct) =>
        store.InTransactionAsync(true, async token =>
        {
            var (subject, person, data) = await ReadInputsAsync(id, write: false, token, track: true);
            var now = clock.GetUtcNow();
            SyncCalendar(subject, person, now);
            Audit(person.PatientId, "person.read", now);
            return ToPerson(subject, person, data, LocalDate(subject, now));
        }, ct);

    internal async Task<T> CalculateAsync<T>(Guid id, Func<MotorView, MotorCalculation, T> project, CancellationToken ct,
        int? requestedDay = null, string? readPermission = null, bool includeProjections = true)
    {
        var inputs = await store.InTransactionAsync(true, async token =>
        {
            var (subject, person, data) = await ReadInputsAsync(id, write: false, token, track: true, readPermission);
            var now = clock.GetUtcNow();
            SyncCalendar(subject, person, now);
            var dto = ToPerson(subject, person, data, LocalDate(subject, now));
            RequirePlan(dto.Status);
            ValidateRequestedDay(requestedDay, dto.Day);
            var parameters = data.ParamsVersion.HasValue ? await store.FindParamsAsync(data.ParamsVersion, token) : null;
            if (data.ParamsVersion.HasValue && parameters is null)
                throw new MotorException("service_unavailable");
            return (person, data, dto, parameters);
        }, ct);
        return project(new(inputs.dto, inputs.dto.Day), Calculate(inputs.person, inputs.data, inputs.dto.Day,
            inputs.parameters, ct, includeProjections));
    }

    public Task<MotorCalendarDay> ReadCalendarDayAsync(Guid id, int? day, CancellationToken ct) =>
        CalculateAsync(id, (view, computation) =>
        {
            var value = computation.Result.Days[(day ?? view.Day) - 1];
            var recorded = computation.Profile.Logs.ContainsKey(value.Day);
            var streak = value.Streak ?? (value.Day > 1 ? computation.Result.Days[value.Day - 2].Streak : null) ?? 0;
            var state = value.Paused ? "paused" : value.Mode == WellnessMode.Maintain ? "maint" : "active";
            return new MotorCalendarDay(value.Day, view.Day, view.Person.TodayLocalDate, view.Person.StartDate,
                view.Person.TimeZoneId, state, recorded ? "recorded" : value.Past ? "missing" : "open",
                recorded || value.Past ? value.Log.Adherence : null, value.Log.MeasuredWeightKg, streak, computation.Version);
        }, ct, day, "Program.Profile360View", includeProjections: false);

    private static void ValidateRequestedDay(int? requested, int today)
    {
        if (requested is < 1 or > WellnessEngine.PlanLength) throw new MotorException("bad_day");
        if (requested > today) throw new MotorException("future_day", issues: [new("future_day", "day")]);
    }

    public Task<MotorLogResult> CloseDayAsync(PutMotorLog request, CancellationToken ct) =>
        store.InTransactionAsync(true, async token =>
        {
            var (subject, person, data) = await ReadInputsAsync(request.Id, write: true, token);
            var now = clock.GetUtcNow();
            SyncCalendar(subject, person, now);
            var day = Day(person, LocalDate(subject, now));
            if (data.Receipts is null) RequirePlan(Status(person, data, day));
            var logs = data.Logs.ToDictionary(log => log.Day);
            if (ApplyLog(person, day, logs, request.Day, request.Input))
            {
                person.PlanVersion++;
                var revision = data with { Logs = logs.Values.ToArray() };
                if (Status(person, revision, day).Ok)
                    await PublishAsync(person, revision, day, request.Day, now, token);
                Audit(person.PatientId, "log.put", now);
            }
            return new MotorLogResult(true, person.PlanVersion);
        }, ct);

    private async Task<MotorSubject> SubjectAsync(Guid? requested, bool write, CancellationToken ct, string? readPermission = null)
    {
        var user = actor.UserId ?? throw new MotorException("forbidden");
        Guid id;
        if (actor.IsApp)
        {
            id = await scope.EnsureSelfAsync(user, ct);
            if (requested.HasValue && requested != id) throw new MotorException("person_not_found");
        }
        else
        {
            id = requested ?? throw new MotorException("invalid_request", issues: [new("invalid_field", "id")]);
            await scope.EnsureAsync(id, write ? "Wellness.Manage" : readPermission ?? "Wellness.View", ct);
        }
        return await store.FindSubjectAsync(id, ct) ?? throw new MotorException("person_not_found");
    }

    private async Task<(MotorSubject Subject, Motor365Person Person, MotorData Data)> ReadInputsAsync(
        Guid id, bool write, CancellationToken ct, bool track = false, string? readPermission = null)
    {
        var subject = await SubjectAsync(id, write, ct, readPermission);
        var data = (await store.LoadAsync(id, write || track, ct)) with { BirthDate = subject.BirthDate };
        return (subject, data.Person ?? throw new MotorException("person_not_found"), data);
    }

    private async Task PublishAsync(
        Motor365Person person, MotorData data, int day, int firstObservedDay, DateTimeOffset now, CancellationToken ct)
    {
        // La revisión conserva sus parámetros. Una revisión nueva adopta la versión vigente.
        var parameters = await store.FindParamsAsync(null, ct);
        if (parameters is null)
        {
            parameters = MotorParameterVersions.Initial(now, actor.UserId!.Value);
            store.AddParams(parameters);
        }
        var calculation = Calculate(person, data, day, parameters, ct);
        store.AddSnapshot(new()
        {
            PersonId = person.PatientId, PlanVersion = person.PlanVersion, ParamsVersion = calculation.Version,
            SubmissionId = data.Screening!.SubmissionId, GeneratedAt = now,
            PlanJson = JsonSerializer.Serialize(new
            {
                Plan = new MotorPlanDto(person.PatientId, day, person.PlanVersion,
                calculation.Result.Days.Select(d => MotorProjection.Day(d, calculation.Version)).ToArray(),
                                    person.EngineVer, calculation.Version),
                ConsentReceiptIds = new[] { data.Receipts!.Terms.Id, data.Receipts.Privacy.Id },
            }, JsonSerializerOptions.Web),
        });
        store.AddEvents(calculation.Result.Events.Where(e => e.Day <= day).Select(e =>
        {
            var view = MotorProjection.Event(e, calculation.Version);
            return new Motor365EngineEvent
            {
                PersonId = person.PatientId, Day = e.Day, PlanVersion = person.PlanVersion,
                ParamsVersion = calculation.Version, Kind = e.Kind, CalorieDelta = (decimal?)e.CalorieDelta,
                RatePercent = (decimal?)e.RatePercent, MessageEs = view.Es, MessageEn = view.En, OccurredAt = now,
            };
        }));
        // Las fechas previas a una revisión conservan la decisión que ya quedó registrada.
        var pauseDays = calculation.Result.Days
            .Where(item => item.Day >= firstObservedDay && item.Day <= day)
            .Select(item => new Motor365PauseDay
            {
                PersonId = person.PatientId, Day = item.Day, Date = person.StartDate.AddDays(item.Day - 1),
                Scheduled = item.Mode == WellnessMode.Break, SafetyExercise = item.Paused, PlanVersion = person.PlanVersion
            }).ToArray();
        await store.SavePauseDaysAsync(person.PatientId, pauseDays, ct);
    }

    private static MotorCalculation Calculate(Motor365Person person, MotorData data, int day,
        Motor365EngineParams? stored, CancellationToken ct, bool includeProjections = true)
    {
        if (person.EngineVer != WellnessEngine.ReferenceVersion)
            throw new MotorException("service_unavailable");
        var version = stored?.Version ?? WellnessEngine.ReferenceParametersVersion;
        var profile = Profile(person, day, data.BirthDate) with
        {
            Parameters = MotorParameterVersions.Read(stored),
            Logs = data.Logs.ToDictionary(log => log.Day, log =>
                new WellnessDailyLog((double)log.Adherence, (double)log.SleepHours, log.Energy, log.Pain,
                    (double?)log.WeightKg, log.CompletedExercise)
                { Symptoms = log.Symptoms }),
        };
        return new(profile, WellnessEngine.EvaluateRecordedTimeline(profile, ct, includeProjections), version);
    }

    private static WellnessProfile Profile(Motor365Person person, int day, DateTime? birth) =>
        new(person.Sex, Age(person, birth), (double)person.HeightCm,
            (double)person.InitialWeightKg, (double)person.InitialWaistCm, (double?)person.TargetWeightKg,
            person.ActivityLevel, person.Goal, day, BinaryPrimitives.ReadUInt32LittleEndian(person.PatientId.ToByteArray()))
        {
            Pace = person.Pace, FitnessLevel = person.FitnessLevel,
            Vegetarian = person.Vegetarian, LactoseFree = person.LactoseFree,
        };

    private static MotorStatus Status(Motor365Person person, MotorData data, int day)
    {
        if (data.Receipts is null) return Block("terms", "Falta confirmar los términos vigentes.", "Confirm the current terms to continue.");
        if (data.Screening is not { HasCompleteAnswers: true } screening)
            return Block("unanswered", "Faltan respuestas en el formulario.", "Complete the remaining answers.");
        if (WellnessEngine.CheckBlock(Profile(person, day, data.BirthDate)) is { } block)
            return block.Code == "age"
                ? Block("age", "El programa está disponible para mayores de 18 años.", "The program is available to adults aged 18 and over.")
                : Block("bmi", "Revisa los datos y el objetivo para continuar.", "Review your details and goal to continue.");
        if (!data.Approved && (ReadParq(screening)!.Contains((short?)1) || ReadNqs(screening)!.Contains((short?)1)))
            return Block("doctor", "El formulario está pendiente de revisión.", "Your form is awaiting review.");
        return new(true);

        static MotorStatus Block(string code, string es, string en) => new(false, code, es, new(es, en));
    }

    private static MotorException Gate(string field) =>
        new("eligibility_required", issues: [new("terms", field, 6)]);

    private static void RequirePlan(MotorStatus status)
    {
        if (!status.Ok) throw new MotorException("plan_locked", status);
    }

    private bool ApplyLog(Motor365Person person, int today, Dictionary<int, Motor365DailyLog> logs, int day, MotorLogInput input)
    {
        ValidateRequestedDay(day, today);
        input = input with { Adh = Decimal.Round(input.Adh!.Value, 4, MidpointRounding.AwayFromZero),
            Sleep = Decimal.Round(input.Sleep!.Value, 2, MidpointRounding.AwayFromZero),
            W = input.W is decimal w ? Decimal.Round(w, 2, MidpointRounding.AwayFromZero) : null,
            Waist = input.Waist is decimal waist ? Decimal.Round(waist, 2, MidpointRounding.AwayFromZero) : null,
            Sym = (input.Sym ?? []).Distinct().Order(StringComparer.Ordinal).ToArray() };
        if (logs.TryGetValue(day, out var log) && LogInput(log) with { Sym = null } == input with { Sym = null, Ex = input.Ex ?? 0 }
            && log.Symptoms.SequenceEqual(input.Sym ?? []))
            return false;
        if (log is null)
        {
            log = new() { PersonId = person.PatientId, Day = day, LogDate = person.StartDate.AddDays(day - 1) };
            logs.Add(day, log);
            store.AddLog(log);
        }
        log.Adherence = input.Adh!.Value;
        log.SleepHours = input.Sleep!.Value;
        log.Energy = input.Energy!.Value;
        log.Pain = input.Pain!.Value;
        log.Symptoms = input.Sym ?? [];
        log.WeightKg = input.W;
        log.WaistCm = input.Waist;
        log.CompletedExercise = input.Ex == 1;
        return true;
    }

    private static MotorLogInput LogInput(Motor365DailyLog log) =>
        new(log.Adherence, log.SleepHours, log.Energy, log.Pain, log.Symptoms,
            log.WeightKg, log.WaistCm, log.CompletedExercise ? 1 : 0);

    private static void ApplyProfile(Motor365Person p, MotorPersonInput input)
    {
        if (input.Sex is { } sex) p.Sex = sex == "F" ? WellnessSex.Female : WellnessSex.Male;
        if (input.Age is { } age) p.BirthYear = p.StartDate.Year - age;
        p.HeightCm = input.H ?? p.HeightCm;
        p.InitialWeightKg = input.W0 ?? p.InitialWeightKg;
        p.InitialWaistCm = input.Waist0 ?? p.InitialWaistCm;
        p.TargetWeightKg = input.Target ?? p.TargetWeightKg;
        p.ActivityLevel = input.Act ?? p.ActivityLevel;
        p.Goal = input.Goal switch { "bajar" => WellnessGoal.LoseWeight, "masa" => WellnessGoal.GainMuscle, "def" => WellnessGoal.Tone, _ => p.Goal };
        p.Pace = input.Pace switch { "s" => WellnessPace.Slow, "m" => WellnessPace.Moderate, "max" => WellnessPace.Maximum, _ => p.Pace };
        if (input.Veg.HasValue) p.Vegetarian = input.Veg == 1;
        if (input.Nolac.HasValue) p.LactoseFree = input.Nolac == 1;
    }

    private static bool ProfileChanged(Motor365Person p, MotorPersonDto before, DateTime? birth) =>
        p.Sex != (before.Sex == "F" ? WellnessSex.Female : WellnessSex.Male) ||
        Age(p, birth) != before.Age || p.HeightCm != before.H ||
        p.InitialWeightKg != before.W0 || p.InitialWaistCm != before.Waist0 ||
        p.TargetWeightKg != before.Target || p.ActivityLevel != before.Act ||
        Goal(p.Goal) != before.Goal || Pace(p.Pace) != before.Pace ||
        p.Vegetarian != (before.Veg == 1) || p.LactoseFree != (before.Nolac == 1);

    private static MotorPersonDto ToPerson(MotorSubject subject, Motor365Person p, MotorData data, DateOnly local)
    {
        var day = Day(p, local);
        return new(p.PatientId, subject.Name, p.Sex == WellnessSex.Female ? "F" : "M",
            Age(p, data.BirthDate), p.HeightCm, p.InitialWeightKg, p.InitialWaistCm,
            p.TargetWeightKg, p.ActivityLevel, Goal(p.Goal), Pace(p.Pace),
            p.Vegetarian ? 1 : 0, p.LactoseFree ? 1 : 0, ReadParq(data.Screening), ReadNqs(data.Screening),
            data.Approved, data.Approved, data.Receipts is { } receipts ? new(receipts.Terms.AcceptedAt, receipts.Terms.Version) : null,
            p.StartDate, day, p.PlanVersion, data.Screening?.SubmissionId, Status(p, data, day),
            data.Logs.ToDictionary(l => l.Day, LogInput))
        { TodayLocalDate = local, TimeZoneId = subject.Timezone };
    }

    private static int Age(Motor365Person person, DateTime? birth)
    {
        if (birth is not { } known) return person.StartDate.Year - person.BirthYear;
        var date = DateOnly.FromDateTime(known);
        if (date > person.StartDate) return 0;
        var years = person.StartDate.Year - date.Year;
        return date.AddYears(years) > person.StartDate ? years - 1 : years;
    }

    private static string Goal(WellnessGoal goal) => goal switch { WellnessGoal.LoseWeight => "bajar", WellnessGoal.GainMuscle => "masa", _ => "def" };
    private static string Pace(WellnessPace pace) => pace switch { WellnessPace.Slow => "s", WellnessPace.Maximum => "max", _ => "m" };
    private static int Day(Motor365Person p, DateOnly local) =>
        Math.Clamp((p.CalendarAnchorDay ?? 1) + local.DayNumber - (p.CalendarAnchorDate ?? p.StartDate).DayNumber,
            1, WellnessEngine.PlanLength);

    private static void SyncCalendar(MotorSubject subject, Motor365Person person, DateTimeOffset now)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(subject.Timezone, out _))
            throw new MotorException("service_unavailable");
        var previous = new WellnessPlanCalendar(person.StartDate, person.CalendarTimeZoneId ?? subject.Timezone,
            person.CalendarAnchorDate ?? person.StartDate, person.CalendarAnchorDay ?? 1);
        var current = WellnessPlanService.ChangeTimeZone(previous, subject.Timezone, now);
        person.CalendarTimeZoneId = current.TimeZoneId;
        person.CalendarAnchorDate = current.AnchorLocalDate;
        person.CalendarAnchorDay = current.AnchorDay;
    }

    private static DateOnly LocalDate(MotorSubject subject, DateTimeOffset now)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(subject.Timezone, out var timeZone))
            throw new MotorException("service_unavailable");
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
    }

    private static Motor365Screening NewScreening(Guid id, Guid key, DateTimeOffset stamp, short?[] p, short?[] n) =>
        new() { PersonId = id, SubmissionId = key, RecordedAt = stamp, ParQ1 = p[0], ParQ2 = p[1],
            ParQ3 = p[2], ParQ4 = p[3], ParQ5 = p[4], ParQ6 = p[5], ParQ7 = p[6],
            Nqs1 = n[0], Nqs2 = n[1], Nqs3 = n[2], Nqs4 = n[3], Nqs5 = n[4], Nqs6 = n[5] };

    private static short?[]? ReadParq(Motor365Screening? s) => s is null ? null : [s.ParQ1, s.ParQ2, s.ParQ3, s.ParQ4, s.ParQ5, s.ParQ6, s.ParQ7];
    private static short?[]? ReadNqs(Motor365Screening? s) => s is null ? null : [s.Nqs1, s.Nqs2, s.Nqs3, s.Nqs4, s.Nqs5, s.Nqs6];

    private void Audit(Guid id, string action, DateTimeOffset now) =>
        store.AddAudit(new() { PersonId = id, Action = action, OccurredAt = now, ActorUserId = actor.UserId });
}
