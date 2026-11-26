using System.Text.Json;
using CoppAddresd.Application.Features.Motor365;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Wellness365;
using FluentValidation.TestHelper;

using NSubstitute;

namespace CoppAddresd.UnitTests.Wellness365;

public sealed class Motor365HandlerTests
{
    private readonly Guid id = Guid.NewGuid();
    private readonly Guid user = Guid.NewGuid();
    private readonly IMotor365Store store = Substitute.For<IMotor365Store>();
    private readonly IMotor365Actor actor = Substitute.For<IMotor365Actor>();
    private readonly IPatientScopeGuard scope = Substitute.For<IPatientScopeGuard>();
    private readonly TimeProvider clock = new TestClock();

    [Fact]
    public async Task Alta_calcula_plan_y_respeta_el_dia_local()
    {
        SetUp();
        Transactions<MotorPersonWrite>(true);
        store.LoadAsync(id, true, Arg.Any<CancellationToken>()).Returns(new MotorData(null, null, false, null, [], null));
        var receipts = Receipts();
        store.FindCurrentReceiptsAsync(id, user, false, "1.0", Arg.Any<CancellationToken>()).Returns(receipts);
        var handler = new WriteMotorPersonHandler(new(store, actor, scope, clock));
        using var cancellation = new CancellationTokenSource();
        var result = await handler.Handle(new(null, Input()), cancellation.Token);
        Assert.True(result.Created);
        Assert.True(result.Result.Status.Ok);
        Assert.Equal(id, result.Result.Id);
        await store.Received(1).FlushAsync(cancellation.Token);
        store.Received(1).AddPerson(Arg.Is<Motor365Person>(p => p.StartDate == new DateOnly(2026, 11, 25)));
        store.Received(1).AddSnapshot(Arg.Is<Motor365PlanSnapshot>(p =>
            p.PersonId == id && p.SubmissionId == result.Result.SubmissionId && p.ParamsVersion == 1));
    }

    [Fact]
    public async Task Plan_diario_devuelve_cuatro_comidas_y_respeta_la_pausa()
    {
        SetUp();
        Transactions<(Motor365Person, MotorData, MotorPersonDto, Motor365EngineParams?)>(true);
        var log = new Motor365DailyLog { Day = 1, Adherence = .8m, SleepHours = 8, Energy = 5, Pain = 8 };
        var data = Existing() with { Logs = [log] };
        data.Person!.StartDate = data.Person.StartDate.AddDays(-1);
        store.LoadAsync(id, true, Arg.Any<CancellationToken>()).Returns(data);
        var handler = new GetMotorDayHandler(new(store, actor, scope, clock));
        var result = await handler.Handle(new(id, 2), default);
        Assert.True(result.Pause);
        Assert.False(result.Train);
        Assert.Empty(result.Exercise.Items);
        Assert.Equal(4, result.Menu!.Count);
        Assert.All(result.Menu, meal => Assert.NotEmpty(meal.Items));
    }

    [Fact]
    public async Task Consentimiento_revocado_y_paciente_ajeno_no_entregan_plan()
    {
        SetUp();
        Transactions<(Motor365Person, MotorData, MotorPersonDto, Motor365EngineParams?)>(true);
        store.LoadAsync(id, true, Arg.Any<CancellationToken>()).Returns(Existing() with { Receipts = null });
        var handler = new GetMotorPlanHandler(new(store, actor, scope, clock));
        var locked = await Assert.ThrowsAsync<MotorException>(() => handler.Handle(new(id), default));
        Assert.Equal("plan_locked", locked.Code);
        Assert.Equal("terms", locked.Status!.Code);
        var foreign = Guid.NewGuid();
        var hidden = await Assert.ThrowsAsync<MotorException>(() => handler.Handle(new(foreign), default));
        Assert.Equal("person_not_found", hidden.Code);
        await store.DidNotReceive().LoadAsync(foreign, Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cierre_futuro_rechazado_y_repeticion_idempotente()
    {
        SetUp();
        Transactions<MotorLogResult>(true);
        var log = new Motor365DailyLog { Day = 1, Adherence = .8m, SleepHours = 8, Energy = 5, Pain = 0 };
        store.LoadAsync(id, true, Arg.Any<CancellationToken>()).Returns(Existing() with { Logs = [log] });
        var handler = new PutMotorLogHandler(new(store, actor, scope, clock));
        var input = new MotorLogInput(.8m, 8, 5, 0, [], null, null, null);
        var future = await Assert.ThrowsAsync<MotorException>(() => handler.Handle(new(id, 2, input), default));
        Assert.Equal("future_day", future.Code);
        var replay = await handler.Handle(new(id, 1, input), default);
        Assert.Equal(1, replay.Ver);
        store.DidNotReceive().AddAudit(Arg.Any<Motor365AuditLog>());
    }

    [Fact]
    public async Task Parametros_se_versionan_y_el_motor_aplica_el_piso_publicado()
    {
        SetUp();
        Transactions<MotorParamsDto>(true);
        var handler = new PutMotorParamsHandler(store, actor, clock);
        var request = new PutMotorParams(new(JsonSerializer.SerializeToElement(new { floorF = 2000 }), "Prueba", 1));
        var result = await handler.Handle(request, default);
        Assert.Equal(2, result.ParamsVersion);
        Assert.Equal(1200, result.Defaults.FloorFemale);
        var profile = new WellnessProfile(WellnessSex.Female, 40, 165, 70, 85, 65, 1, WellnessGoal.LoseWeight, 1, 1)
            { Parameters = result.Params };
        Assert.Equal(2000, WellnessEngine.Simulate(profile).Days[0].Targets.Kcal);
        store.Received(1).AddParams(Arg.Is<Motor365EngineParams>(p => p.Version == 2 && p.ChangedBy == user));
        var invalid = request with { Input = request.Input with { Params = JsonSerializer.SerializeToElement(new { step = -1 }) } };
        Assert.Equal("invalid_parameters", (await Assert.ThrowsAsync<MotorException>(() => handler.Handle(invalid, default))).Code);
    }

    [Fact]
    public void Validadores_rechazan_rangos_y_aceptan_borradores_del_cuestionario()
    {
        var upsert = new WriteMotorPersonValidator();
        upsert.TestValidate(new WriteMotorPerson(null, Input() with { ParqA = new short?[7] })).ShouldNotHaveAnyValidationErrors();
        upsert.TestValidate(new WriteMotorPerson(null, Input() with { ParqA = [2], Act = 5 })).ShouldHaveValidationErrorFor("Input.ParqA");
        var log = new PutMotorLogValidator();
        log.TestValidate(new PutMotorLog(id, 0, new(null, 25, 6, 11, ["other"], null, null, 2))).ShouldHaveValidationErrorFor(x => x.Day);
        log.TestValidate(new PutMotorLog(id, 1, new(.75m, 7, 4, 0, ["fatiga"], 75, null, 1))).ShouldNotHaveAnyValidationErrors();
    }

    private void SetUp()
    {
        actor.UserId.Returns(user);
        actor.IsApp.Returns(true);
        scope.EnsureSelfAsync(user, Arg.Any<CancellationToken>()).Returns(id);
        store.FindSubjectAsync(id, Arg.Any<CancellationToken>()).Returns(new MotorSubject(id, user, "Prueba", "America/Bogota"));
    }

    private void Transactions<T>(bool write) =>
        store.InTransactionAsync(write, Arg.Any<Func<CancellationToken, Task<T>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<T>>>()(call.Arg<CancellationToken>()));

    private static MotorPersonInput Input() => new(null, Guid.NewGuid(), null, "F", 35, 165, 75, 90, 70, 2, "bajar",
        "m", 0, 0, [0, 0, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0], null, null, new(null, "1.0"), null, null, null);

    private MotorReceipts Receipts() => new(new(Guid.NewGuid(), "1.0", clock.GetUtcNow()), new(Guid.NewGuid(), "1.0", clock.GetUtcNow()));

    private MotorData Existing() => new(new Motor365Person { PatientId = id, BirthYear = 1991, ActivityLevel = 2, HeightCm = 165,
        InitialWeightKg = 75, InitialWaistCm = 90, StartDate = new DateOnly(2026, 11, 25) }, new()
        {
            PersonId = id, SubmissionId = Guid.NewGuid(),
            ParQ1 = 0, ParQ2 = 0, ParQ3 = 0, ParQ4 = 0, ParQ5 = 0, ParQ6 = 0, ParQ7 = 0,
            Nqs1 = 0, Nqs2 = 0, Nqs3 = 0, Nqs4 = 0, Nqs5 = 0, Nqs6 = 0,
        }, false, Receipts(), [], null);
    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 26, 2, 0, 0, TimeSpan.Zero);
    }
}
