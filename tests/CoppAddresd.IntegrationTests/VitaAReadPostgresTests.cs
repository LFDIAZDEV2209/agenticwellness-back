using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CoppAddresd.Api.Extensions;
using CoppAddresd.Api.Internal;
using CoppAddresd.Api.Middleware;
using CoppAddresd.Api.Security;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Common.Behaviors;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    private const string VitaAInternalKey = "synthetic-vita-a-http-key";
    private const string VitaARoute = "/api/v1/internal/vita";
    private static readonly DateTimeOffset VitaANow = new(2026, 11, 19, 3, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly VitaAToday = new(2026, 11, 18);

    [NutritionPostgresFact]
    public async Task VitaA_Profile_IncluyeMetasAlergiasYAntropometriaAisladas()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var other = await PatientAsync(db);
        patient.DateOfBirth = new DateTime(1988, 11, 19);
        patient.Gender = "F";
        patient.DocumentNumber = "SYNTHETIC-DOCUMENT";
        var allergen = new Allergen { Id = Guid.NewGuid(), Name = "Alérgeno de prueba" };
        db.PatientAllergies.Add(new() { PatientId = patient.Id, Allergen = allergen, Notes = "Nota interna" });
        db.PatientAllergies.Add(new() { PatientId = other.Id, Allergen = new() { Name = "Alérgeno ajeno" } });

        var plan = new NutritionPlan
        {
            Id = Guid.NewGuid(), PatientId = patient.Id, IsTemplate = false, Name = "Plan QA", DurationDays = 30,
            Status = NutritionPlanStatus.Active, ReviewStatus = "approved", ApprovedAt = VitaANow.UtcDateTime,
            ApprovedBy = patient.UserId, TargetCondition = "Mantenimiento", DailyProteinTarget = 95m,
            Allergens = "Restricción registrada",
        };
        db.NutritionPlanAssignments.Add(new()
        {
            Id = Guid.NewGuid(), PatientId = patient.Id, Plan = plan,
            StartDate = VitaANow.UtcDateTime.Date.AddDays(-4),
        });
        var (metric, unit) = await VitaAMetricAsync(db, "weight", "kg", "kg", "body_comp");
        var now = VitaANow.UtcDateTime;
        db.VitalSigns.Add(new() { PatientId = patient.Id, MeasuredAt = now.AddDays(-2), HeightCm = 168 });
        db.ClinicalMeasurements.Add(VitaAReading(other.Id, metric, unit, now, 999, "professional"));
        db.ClinicalMeasurements.AddRange(Enumerable.Range(1, 101).Select(i =>
            VitaAReading(patient.Id, metric, unit, now.AddMinutes(-i), 74.5m, "patient")));
        await db.SaveChangesAsync();

        await using var host = await VitaAHostAsync();
        using var client = VitaAClient(host);
        using var response = await client.GetAsync($"{VitaARoute}/profile?userId={patient.UserId}&patientId={other.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        var profile = JsonSerializer.Deserialize<VitaProfileDto>(json, JsonSerializerOptions.Web)!;
        Assert.Contains("\"ageYears\"", json);
        Assert.DoesNotContain("SYNTHETIC-DOCUMENT", json);
        Assert.DoesNotContain("Nota interna", json);
        Assert.Equal(37, profile.Profile.AgeYears);
        Assert.Equal("nutrition_plan", profile.Goals!.Source);
        Assert.Equal(95d, profile.Goals.DailyProteinTargetG);
        Assert.Equal(new[] { "Alérgeno de prueba" }, profile.Allergies);
        Assert.Equal(new[] { "Restricción registrada" }, profile.Restrictions);
        Assert.Equal(74.5m, profile.Anthropometry.Weight!.Value);
        Assert.Equal("patient", profile.Anthropometry.Weight.Source);
        Assert.Equal(168m, profile.Anthropometry.Height!.Value);
        Assert.Null(profile.Anthropometry.Height.Source);
        Assert.Null(profile.Anthropometry.Bmi);
        Assert.Equal(100, profile.Anthropometry.History.Count);
        Assert.True(profile.Anthropometry.HasMore);

        var cycle = new NutritionCycle
        {
            PatientId = patient.Id, State = "en_curso", Number = 1,
            StartDate = VitaAToday.AddDays(-5), EndDate = VitaAToday.AddDays(24),
        };
        db.NutritionCycles.Add(cycle);
        db.NutritionPrescriptions.Add(new()
        {
            PatientId = patient.Id, CycleId = cycle.Id, State = "aprobado", IsPublished = true,
            CreatedBy = patient.UserId!.Value, ApprovedBy = patient.UserId, ApprovedAt = VitaANow.UtcDateTime,
            StartDate = cycle.StartDate, EndDate = cycle.EndDate, CalorieTarget = 1800,
        });
        await db.SaveChangesAsync();
        var published = await client.GetFromJsonAsync<VitaProfileDto>($"{VitaARoute}/profile?userId={patient.UserId}");
        Assert.Equal("nutrition_prescription", published!.Goals!.Source);
        Assert.Equal(1800d, published.Goals.DailyCalorieTarget);
        Assert.Empty(published.Restrictions);
    }

    [NutritionPostgresFact]
    public async Task VitaA_Vitals_RespetaDiaLocalUnidadOrigenYCorreccionesConfirmadas()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var other = await PatientAsync(db);
        var start = new DateTime(2026, 11, 18, 5, 0, 0, DateTimeKind.Utc);
        var (pulse, bpm) = await VitaAMetricAsync(db, "heart_rate", "bpm", "bpm", "vital");
        var (pressure, mmhg) = await VitaAMetricAsync(db, "systolic_bp", "mmhg", "mmHg", "vital");
        var (oxygen, pct) = await VitaAMetricAsync(db, "o2_saturation", "pct", "%", "vital");
        var (sleep, min) = await VitaAMetricAsync(db, "sleep_minutes", "minutes", "min", "lifestyle");
        var (glucose, mg) = await VitaAMetricAsync(db, "glucose_fasting", "mg_dl", "mg/dL", "metabolic");
        var (steps, count) = await VitaAMetricAsync(db, "step_count", "count", "u", "activity");
        var initial = VitaAReading(patient.Id, pressure, mmhg, start.AddHours(3), 114, "patient");
        var correction = VitaAReading(patient.Id, pressure, mmhg, start.AddHours(4), 117, "patient");
        correction.SupersedesId = initial.Id;
        var pending = VitaAReading(patient.Id, oxygen, pct, start.AddHours(8), 99, "patient");
        pending.Status = "pending_confirmation";
        db.ClinicalMeasurements.AddRange(
            initial, correction, pending,
            VitaAReading(patient.Id, pulse, bpm, start.AddSeconds(-1), 109, "device"),
            VitaAReading(patient.Id, sleep, min, start, 440, "device"),
            VitaAReading(patient.Id, pulse, bpm, start.AddHours(1), 65, "device"),
            VitaAReading(patient.Id, pulse, bpm, start.AddHours(2), 68, "professional"),
            VitaAReading(patient.Id, glucose, mg, start.AddHours(7), 89, "lab"),
            VitaAReading(patient.Id, steps, count, start.AddHours(8), 7890, "device"),
            VitaAReading(other.Id, pulse, bpm, start.AddHours(9), 111, "device"));
        await db.SaveChangesAsync();

        await using var host = await VitaAHostAsync();
        using var client = VitaAClient(host);
        var route = $"{VitaARoute}/vitals?userId={patient.UserId}";
        var day = await client.GetFromJsonAsync<VitaVitalsDto>(route + "&from=2026-11-18&to=2026-11-18");
        Assert.Equal(VitaAToday, day!.From);
        Assert.Equal(VitaAToday, day.To);
        Assert.Equal("America/Bogota", day.TimeZone);
        Assert.Equal(6, day.History.Count);
        Assert.Equal(new[] { "device", "lab", "patient", "professional" },
            day.History.Select(x => x.Source).Distinct().Order());
        var latestPulse = Assert.Single(day.Latest, x => x.MetricCode == "heart_rate");
        Assert.Equal(68m, latestPulse.Value);
        Assert.Equal("bpm", latestPulse.UnitCode);
        Assert.Equal(DateTimeKind.Utc, latestPulse.ObservedAt.Kind);
        Assert.Equal(117m, Assert.Single(day.History, x => x.MetricCode == "systolic_bp").Value);
        Assert.Equal(start, Assert.Single(day.History, x => x.MetricCode == "sleep_minutes").ObservedAt);
        Assert.DoesNotContain(day.Latest, x => x.MetricCode == "o2_saturation");
        Assert.False(day.HasMore);

        var defaults = await client.GetFromJsonAsync<VitaVitalsDto>(route);
        Assert.Equal(VitaAToday.AddDays(-6), defaults!.From);
        Assert.Equal(VitaAToday, defaults.To);
        using var wide = await client.GetAsync(route + "&from=2026-01-01&to=2026-11-18");
        Assert.Equal(HttpStatusCode.BadRequest, wide.StatusCode);
        using var malformed = await client.GetAsync(route + "&from=invalid");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
    }

    [NutritionPostgresFact]
    public async Task VitaA_Medications_UsaElModuloYTomasDelDiaSinEscribir()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var other = await PatientAsync(db);
        var clock = new VitaAClock();
        var store = new MedicationControlStore(db, Substitute.For<IPatientScopeGuard>(), clock);
        Task<MedicationCreateResult> Create(PatientProfile owner, string name, string product, DateOnly start, DateOnly? end = null)
            => store.CreateAsync(owner.Id, owner.UserId!.Value, "patient_reported",
                new(Guid.NewGuid(), null, name, product, 1, "unidad", "oral", "Nota interna", ["08:00", "20:00"],
                    "America/Bogota", start, end), default);

        var medication = (await Create(patient, "Medicamento QA", "medication", VitaAToday.AddDays(-1))).Treatment;
        var supplement = (await Create(patient, "Nutracéutico QA", "supplement", VitaAToday.AddDays(-1))).Treatment;
        await Create(patient, "Futuro", "medication", VitaAToday.AddDays(1));
        await Create(patient, "Vencido", "medication", VitaAToday.AddDays(-5), VitaAToday.AddDays(-1));
        var paused = (await Create(patient, "Pausado", "vitamin", VitaAToday.AddDays(-1))).Treatment;
        await store.ChangeStatusAsync(patient.Id, paused.Id, new("paused", paused.Revision), true, default);
        await Create(other, "Ajeno", "medication", VitaAToday);
        var workspace = await store.WorkspaceAsync(patient.Id, VitaAToday, "America/Bogota", true, default);
        var dose = workspace.Doses.First(d => d.TreatmentId == medication.Id);
        await store.RecordAsync(patient.Id, patient.UserId!.Value, "patient_reported", dose.Id,
            new(Guid.NewGuid(), "taken", dose.ScheduledAt.AddMinutes(10), ScheduledAt: dose.ScheduledAt), default);

        var counts = await VitaAMedicationCountsAsync(db);
        await using var host = await VitaAHostAsync();
        using var client = VitaAClient(host);
        using var response = await client.GetAsync($"{VitaARoute}/medications?userId={patient.UserId}&patientId={other.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Nota interna", json);
        var result = JsonSerializer.Deserialize<VitaMedicationsDto>(json, JsonSerializerOptions.Web)!;
        Assert.Equal(VitaAToday, result.LocalDate);
        Assert.Equal("America/Bogota", result.TimeZone);
        Assert.Equal(new[] { medication.Id, supplement.Id }.Order(), result.Treatments.Select(t => t.Id).Order());
        var treatment = Assert.Single(result.Treatments, t => t.Id == medication.Id);
        Assert.Equal(new[] { "08:00", "20:00" }, treatment.Times);
        Assert.Equal(new[] { "taken", "pending" }, treatment.Doses.Select(d => d.Status));
        Assert.Equal(dose.ScheduledAt.AddMinutes(10), treatment.Doses[0].OccurredAt);
        Assert.Equal(new DateTime(2026, 11, 19, 1, 0, 0, DateTimeKind.Utc), treatment.Doses[1].ScheduledAt);
        Assert.Null(treatment.Doses[1].OccurredAt);
        Assert.Equal("supplement", Assert.Single(result.Treatments, t => t.Id == supplement.Id).ProductType);
        Assert.Equal(counts, await VitaAMedicationCountsAsync(db));
    }

    [NutritionPostgresFact]
    public async Task VitaA_Lecturas_ProtegenIdentidadConsentimientoYFlags()
    {
        await using var db = Context();
        var patient = await PatientAsync(db);
        var denied = await PatientAsync(db);
        var inactive = await PatientAsync(db);
        inactive.Status = "Inactivo";
        db.PatientModuleAccess.Add(new() { PatientId = patient.Id, Enabled = false, UpdatedBy = patient.UserId!.Value,
            UpdatedAt = VitaANow.UtcDateTime, Revision = 1 });
        await db.SaveChangesAsync();
        await using var host = await VitaAHostAsync(deniedUser: denied.UserId);
        using var anonymous = VitaAClient(host, attachKey: false);
        using var client = VitaAClient(host);
        foreach (var path in new[] { "profile", "vitals", "medications" })
        {
            using var noKey = await anonymous.GetAsync($"{VitaARoute}/{path}?userId={patient.UserId}");
            Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
            using var missing = await client.GetAsync($"{VitaARoute}/{path}");
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            using var empty = await client.GetAsync($"{VitaARoute}/{path}?userId={Guid.Empty}");
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            using var unknown = await client.GetAsync($"{VitaARoute}/{path}?userId={Guid.NewGuid()}");
            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
            Assert.Contains("VITA_PATIENT_UNAVAILABLE", await unknown.Content.ReadAsStringAsync());
            using var inactiveUser = await client.GetAsync($"{VitaARoute}/{path}?userId={inactive.UserId}");
            Assert.Equal(HttpStatusCode.Forbidden, inactiveUser.StatusCode);
            using var revoked = await client.GetAsync($"{VitaARoute}/{path}?userId={denied.UserId}");
            Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
            Assert.Contains("CONSENT_REQUIRED", await revoked.Content.ReadAsStringAsync());
        }
        using var medications = await client.GetAsync($"{VitaARoute}/medications?userId={patient.UserId}");
        Assert.Equal(HttpStatusCode.Forbidden, medications.StatusCode);
        Assert.Contains("VITA_MEDICATIONS_FORBIDDEN", await medications.Content.ReadAsStringAsync());
        var emptyVitals = await client.GetFromJsonAsync<VitaVitalsDto>($"{VitaARoute}/vitals?userId={patient.UserId}");
        Assert.Empty(emptyVitals!.Latest);
        Assert.Empty(emptyVitals.History);

        await using var disabled = await VitaAHostAsync(internalEnabled: false);
        using var disabledClient = VitaAClient(disabled);
        foreach (var path in new[] { "profile", "vitals", "medications" })
        {
            using var flag = await disabledClient.GetAsync($"{VitaARoute}/{path}?userId={patient.UserId}");
            Assert.Equal(HttpStatusCode.NotFound, flag.StatusCode);
        }
        await using var disabledMedications = await VitaAHostAsync(medicationEnabled: false);
        using var disabledMedicationsClient = VitaAClient(disabledMedications);
        using var medicationFlag = await disabledMedicationsClient.GetAsync($"{VitaARoute}/medications?userId={patient.UserId}");
        Assert.Equal(HttpStatusCode.NotFound, medicationFlag.StatusCode);
    }

    private async Task<WebApplication> VitaAHostAsync(bool internalEnabled = true, bool medicationEnabled = true,
        Guid? deniedUser = null, bool captureEnabled = false, TimeProvider? clock = null, IProgramMetricsQueue? metrics = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Configuration["Vita:InternalEnabled"] = internalEnabled.ToString();
        builder.Configuration["Vita:CaptureEnabled"] = captureEnabled.ToString();
        if (captureEnabled) builder.Configuration["Vita:CaptureProposalSecret"] = Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        builder.Services.AddControllers().AddApplicationPart(typeof(InternalVitaController).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.Configure<AiServiceSettings>(o => o.InternalApiKey = VitaAInternalKey);
        builder.Services.Configure<MedicationControlSettings>(o => o.Enabled = medicationEnabled);
        builder.Services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<GetVitaProfileQuery>();
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        builder.Services.AddValidatorsFromAssemblyContaining<GetVitaProfileQuery>();
        builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
        builder.Services.AddScoped<IPatientRepository, PatientRepository>();
        builder.Services.AddScoped<IProgramRepository>(sp => EffectiveRepository(sp.GetRequiredService<AppDbContext>()));
        builder.Services.AddScoped<INutritionPrescriptionRepository, NutritionPrescriptionRepository>();
        builder.Services.AddScoped<INutritionRecommendationStore, NutritionRecommendationStore>();
        builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
        builder.Services.AddSingleton(Substitute.For<IObjectStorageService>());
        builder.Services.AddSingleton(new StorageSignatureService("synthetic-vita-document-signature-for-tests"));
        if (metrics is not null) builder.Services.AddSingleton(metrics);
        builder.Services.AddVita(builder.Configuration);
        builder.Services.AddScoped<IMedicationControlStore, MedicationControlStore>();
        builder.Services.AddScoped<MedicationControlService>();
        builder.Services.AddScoped<IClinicalMeasurementRepository, ClinicalMeasurementRepository>();
        builder.Services.AddSingleton(clock ?? new VitaAClock());
        builder.Services.AddSingleton(Substitute.For<ICacheService>());
        builder.Services.AddSingleton(Substitute.For<IPatientScopeGuard>());
        var consent = Substitute.For<ILegalConsentService>();
        consent.EnsureUserAsync(Arg.Any<Guid>(), "chat", Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Guid>() == deniedUser
                ? Task.FromException(new AiConsentException("CONSENT_REQUIRED", "chat", 403))
                : Task.CompletedTask);
        builder.Services.AddSingleton(consent);
        var app = builder.Build();
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private static HttpClient VitaAClient(WebApplication app, bool attachKey = true)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        if (attachKey) client.DefaultRequestHeaders.Add("X-Internal-Key", VitaAInternalKey);
        return client;
    }

    private static async Task<(Guid Metric, Guid Unit)> VitaAMetricAsync(AppDbContext db, string code, string unit,
        string symbol, string category)
    {
        var unitOfMeasure = await db.UnitOfMeasures.SingleOrDefaultAsync(x => x.Code == unit);
        if (unitOfMeasure is null)
        {
            unitOfMeasure = new UnitOfMeasure { Id = Guid.NewGuid(), Code = unit, Name = unit, Symbol = symbol };
            db.UnitOfMeasures.Add(unitOfMeasure);
        }
        var metric = await db.MeasurementMetrics.SingleOrDefaultAsync(x => x.Code == code);
        if (metric is null)
        {
            metric = new MeasurementMetric { Id = Guid.NewGuid(), Code = code, Name = code, Category = category,
                DefaultUnitId = unitOfMeasure.Id };
            db.MeasurementMetrics.Add(metric);
        }
        await db.SaveChangesAsync();
        return (metric.Id, unitOfMeasure.Id);
    }

    private static ClinicalMeasurement VitaAReading(Guid patient, Guid metric, Guid unit, DateTime observed,
        decimal value, string source) => new()
    {
        Id = Guid.NewGuid(), PatientId = patient, MetricId = metric, UnitId = unit, ObservedAt = observed,
        RecordedAt = observed, CreatedAt = observed, Value = value, Source = source,
    };

    private static async Task<(int Doses, int Events, int Audit, int Operations)> VitaAMedicationCountsAsync(AppDbContext db)
        => (await db.MedicationDoses.CountAsync(), await db.MedicationIntakeEvents.CountAsync(),
            await db.MedicationAuditEntries.CountAsync(), await db.MedicationDoseOperations.CountAsync());

    private sealed class VitaAClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => VitaANow;
    }
}
