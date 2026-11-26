using CoppAddresd.Application.Features.LegalDocuments;
using CoppAddresd.Api.Configuration;
using CoppAddresd.Application.Features.Motor365;
using CoppAddresd.Infrastructure.Repositories;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Api.Features.Wellness365;
using CoppAddresd.Application.Features.Wellness365;
using CoppAddresd.Domain.Wellness365;

var builder = WebApplication.CreateBuilder(args);
var fixtureAuth = new FixturePlanAuth();
fixtureAuth.Configure(builder.Services);
var clock = new FixtureClock();
var service = new WellnessPlanService(clock);
var logs = new Dictionary<int, WellnessDailyLog>();
var scenario = new Scenario();
var calendar = service.Activate("America/Bogota");
var fixtureConnection = Environment.GetEnvironmentVariable("COP_KAN131_TEST_DB_CONNECTION");
var postgres = fixtureConnection is null ? null : new FixturePostgres(fixtureConnection);
var patientId = Guid.Empty;
Guid[] cohort = [];
builder.Services.AddControllers().AddApplicationPart(typeof(Motor365PlanController).Assembly);
builder.Services.AddMotor365Api();
builder.Services.AddMediatR(registration => registration.RegisterServicesFromAssemblyContaining<MotorPeople>());
builder.Services.AddScoped<IPatientScopeStore>(_ => new FixturePatientScope(() => patientId, () => cohort));
builder.Services.AddScoped<IPatientScopeGuard, PatientScopeGuard>();
builder.Services.AddScoped(_ => new WellnessPlanService(clock));

if (postgres is not null)
{
    builder.Services.AddScoped(_ => postgres.Open());
    builder.Services.AddSingleton<TimeProvider>(clock);
    builder.Services.AddScoped<IAccountLegalConsentService, FixtureAccountConsent>();
    builder.Services.Configure<AccountLegalConsentSettings>(options =>
    {
        options.EnforcementEnabled = true;
        options.Documents["es"] = new() { Terms = "fixture-account-terms", Privacy = "fixture-account-privacy" };
    });
    builder.Services.AddScoped<CoppAddresd.Infrastructure.Services.WellnessJourneyStore>();
    builder.Services.AddScoped<IWellnessPlanGate, CoppAddresd.Infrastructure.Repositories.WellnessPlanGate>();
    builder.Services.AddScoped<IMotor365Actor, FixtureMotorActor>();
    builder.Services.AddScoped<IMotor365Store, Motor365Store>();
    builder.Services.AddScoped<MotorPeople>();
}
var app = builder.Build();
app.UseWhen(context => context.Request.Path.StartsWithSegments("/v1"),
    pipeline => pipeline.UseMiddleware<Motor365Errors>());
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/fixture", () => Results.Content("""
    <!doctype html><html lang="es"><meta charset="utf-8"><title>Contrato Motor 365</title>
    <label>Día del plan <input name="day"></label><button>Consultar</button>
    <p role="status"></p><p id="weight"></p>
    <script>
    document.querySelector('button').onclick = async () => {
      const day = document.querySelector('input').value;
      const path = '/fixture/plan' + (day ? '/' + encodeURIComponent(day) : '');
      const r = await fetch(path);
      const data = await r.json();
      document.querySelector('[role=status]').textContent = r.ok
        ? 'Día ' + data.today + ' · ' + data.status?.state
        : [r.status, data.error, data.status?.code].filter(Boolean).join(' ');
      document.querySelector('#weight').textContent = data.measuredWeightKg == null
        ? 'Sin pesaje' : data.measuredWeightKg + ' kg';
    };
    </script></html>
    """, "text/html; charset=utf-8"));

app.MapPost("/fixture/reset", async (Scenario value, CancellationToken ct) =>
{
    scenario = value;
    clock.Set(value.Instant ?? FixtureClock.Start);
    service = value.DemoHost ? WellnessPlanService.CreateDemo(clock) : new(clock);
    calendar = service.Activate(value.TimeZone ?? "America/Bogota", value.StartDate);
    logs.Clear();
    if (value.Postgres)
    {
        if (postgres is null) return Results.BadRequest();
        patientId = await postgres.SeedAsync(value, calendar, ct);
    }
    return Results.NoContent();
});

app.MapPost("/fixture/clear-memory", () =>
{
    logs.Clear();
    calendar = service.Activate("America/Bogota");
    return Results.NoContent();
});

app.MapPut("/fixture/clock", (ClockChange change) => { clock.Set(change.Instant); return Results.NoContent(); });
app.MapPut("/fixture/zone", async (ZoneChange change, CancellationToken ct) =>
{
    if (scenario.Postgres)
    {
        await using var db = postgres!.Open();
        await postgres.Store(db, clock).ChangeTimeZoneAsync(patientId, patientId, change.TimeZone, ct);
    }
    else
        calendar = service.ChangeTimeZone(calendar, change.TimeZone);
    return Results.NoContent();
});

app.MapPut("/fixture/profile-zone", async (ZoneChange change, CancellationToken ct) =>
{
    if (postgres is null) return Results.NotFound();
    await postgres.ProfileTimeZoneAsync(patientId, change.TimeZone, ct);
    return Results.NoContent();
});

app.MapGet("/fixture/session/{client}", (string client) => new
{
    accessToken = fixtureAuth.Token(client),
    userId = client == "app" ? FixturePlanAuth.AppUser : FixturePlanAuth.ErpUser,
    patientId,
});

if (postgres is not null && Environment.GetEnvironmentVariable("COP_KAN128_DIAGNOSTICS") == "1")
{
    var diagnostics = new FixtureMotorDiagnostics();
    app.Lifetime.ApplicationStopped.Register(diagnostics.Dispose);
    app.MapGet("/fixture/kan128/cache-counters", () => diagnostics.Read());
    app.MapGet("/fixture/kan128/annual-trace", () => diagnostics.ReadAnnualTrace());
    app.MapGet("/fixture/kan128/snapshots", async (CancellationToken ct) =>
        Results.Ok(await postgres.SnapshotVersionsAsync(patientId, ct)));
    app.MapPost("/fixture/kan128/withdraw-consent", async (CancellationToken ct) =>
    {
        if (patientId == Guid.Empty) return Results.BadRequest();
        await postgres.WithdrawConsentAsync(patientId, ct);
        return Results.NoContent();
    });
    if (Environment.GetEnvironmentVariable("COP_KAN128_LOAD") == "1")
    {
        app.MapPost("/fixture/kan128/cohort", async (CancellationToken ct) =>
        {
            if (patientId == Guid.Empty) return Results.BadRequest();
            cohort = await postgres.SeedCohortAsync(patientId, ct);
            return Results.Ok(cohort);
        });
    }
}

app.MapGet("/fixture/persistence-stats", async (CancellationToken ct) =>
    scenario.Postgres ? Results.Ok(await postgres!.StatsAsync(patientId, ct)) : Results.NotFound());

WellnessPlanPerson? Person() => !scenario.Person ? null : new(
    new(WellnessSex.Female, 35, 170, 80, 85, scenario.Target ?? 70, 2, WellnessGoal.LoseWeight, 365, 123)
    {
        Real = scenario.Real ?? true,
        Logs = logs
    },
    calendar, scenario.Block is { } code ? new(code, new("Faltan términos.", "Terms are missing.")) : null);

app.MapGet("/fixture/plan/{day?}", async (string? day, CancellationToken ct) =>
{
    var person = Person();
    if (scenario.Postgres)
    {
        await using var db = postgres!.Open();
        person = await postgres.Store(db, clock).ReadAsync(patientId, person?.EligibilityBlock, ct);
    }
    var result = service.GetDay(person, day);
    return result.Error is { } error ? error.ToHttpResult() : Results.Ok(result.Value);
});

app.MapPut("/fixture/logs/{day}", async (string day, LogInput value, CancellationToken ct) =>
{
    if (scenario.Postgres)
    {
        await using var db = postgres!.Open();
        var stored = await postgres.Store(db, clock).CloseAsync(patientId, patientId, day,
            new((decimal)value.Adherence, 7, 4, value.Pain, (decimal?)value.Weight, null, true, []), ct);
        return stored.Error is { } failed ? failed.ToHttpResult() : Results.NoContent();
    }
    var result = service.ValidateClosure(Person(), day);
    if (result.Error is { } error) return error.ToHttpResult();
    logs[result.Value!.Day] = new(value.Adherence, 7, 4, value.Pain, value.Weight, true);
    return Results.NoContent();
});

app.Run();

sealed record Scenario(bool Person = true, bool DemoHost = false, bool? Real = null, string? Block = null,
    DateOnly? StartDate = null, DateTimeOffset? Instant = null, string? TimeZone = null, double? Target = null,
    bool Postgres = false, WellnessGoal Goal = WellnessGoal.LoseWeight);
sealed record ClockChange(DateTimeOffset Instant);
sealed record ZoneChange(string TimeZone);
sealed record LogInput(double Adherence, double? Weight, int Pain = 0);

sealed class FixtureClock : TimeProvider
{
    internal static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T18:00:00Z");
    private readonly object _clockLock = new();
    private DateTimeOffset _now = Start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_clockLock) return _now;
    }

    public void Set(DateTimeOffset instant)
    {
        lock (_clockLock) _now = instant;
    }
}
