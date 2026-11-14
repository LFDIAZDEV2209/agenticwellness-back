using System.Text.Json;
using CoppAddresd.Application.DTOs.ProgramProgress;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.HealthTests;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Enums.HealthTests;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CoppAddresd.UnitTests.Features.Agents;

public sealed class VitaContextTests
{
    private readonly IPatientRepository patients = Substitute.For<IPatientRepository>();
    private readonly ILegalConsentService consent = Substitute.For<ILegalConsentService>();
    private readonly IProgramRepository programs = Substitute.For<IProgramRepository>();
    private readonly IWellnessRepository wellness = Substitute.For<IWellnessRepository>();
    private readonly INutritionPrescriptionRepository prescriptions = Substitute.For<INutritionPrescriptionRepository>();
    private readonly IProgramContentResolver content = Substitute.For<IProgramContentResolver>();
    private readonly IHealthTestRepository tests = Substitute.For<IHealthTestRepository>();
    private readonly INutritionPlanWorkflow nutritionStatus = Substitute.For<INutritionPlanWorkflow>();
    private readonly IPatientMeasurementRepository devices = Substitute.For<IPatientMeasurementRepository>();
    private readonly TimeProvider clock = Substitute.For<TimeProvider>();
    private readonly PatientProfile patient = new() { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Status = "Activo", Timezone = "America/Bogota" };
    private readonly Guid enrollmentId = Guid.NewGuid();
    private readonly Guid mediaId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 14);
    private VitaPatientAccess Access => new(patients, consent, clock);
    private GetVitaContextQueryHandler Handler => new(Access, programs, wellness, prescriptions, content,
        tests, nutritionStatus, devices, clock, NullLogger<GetVitaContextQueryHandler>.Instance);

    public VitaContextTests()
    {
        clock.GetUtcNow().Returns(new DateTimeOffset(2026, 11, 15, 2, 0, 0, TimeSpan.Zero));
        patients.GetByUserIdAsync(patient.UserId!.Value, Arg.Any<CancellationToken>()).Returns(patient);
        programs.GetActiveEnrollmentForPatientAsync(patient.Id, Arg.Any<CancellationToken>()).Returns(
            new ProgramEnrollment { Id = enrollmentId, PatientId = patient.Id });
        programs.GetPatientLocalTodayAsync(enrollmentId, Arg.Any<CancellationToken>()).Returns(Today);
        programs.GetSnapshotAsync(enrollmentId, Today, Arg.Any<CancellationToken>()).Returns(Snapshot());
        wellness.ListPlanAssignmentsByPatientAsync(patient.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<NutritionPlanAssignment>());
        tests.ListAssignmentsByPatientAsync(patient.Id, Arg.Any<CancellationToken>()).Returns(Array.Empty<HealthTestAssignment>());
        nutritionStatus.GetStatusAsync(patient.UserId.Value, patient.Timezone, Arg.Any<CancellationToken>())
            .Returns(new NutritionPlanStatusDto("pending_review"));
        devices.GetDeviceWindowAsync(patient.Id, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CoppAddresd.Application.Features.Measurements.Queries.GetMyMeasurements.MeasurementItemDto>());
    }
    private Task<VitaContextDto> Read(string scope, string? version = null, CancellationToken ct = default) =>
        Handler.Handle(new(patient.UserId!.Value, scope, version), ct);

    private ProgramSnapshotDto Snapshot(bool rest = false) => new(enrollmentId,
        new(Guid.NewGuid(), "program", "Programa", 12, 1, ProgramWeekStatus.Active, Today.AddDays(-5), Today.AddDays(1), 3, []),
        Today, [new(TaskCode.podcast, "Podcast", "", 10, "Pending", null,
            new(mediaId, "Episodio asignado", 300, "private/storage/key", Author: "Equipo", MediaUrl: "secret-storage-key"))],
        0, true, 100, new(50, "Inicio", 100), new(1, 2, 0, 1m, null, 0), 7,
        [new(Today, 6, false, 0, "Pending")], Rest: rest);

    [Theory]
    [InlineData("day")]
    [InlineData("podcast")]
    [InlineData("exercise")]
    [InlineData("progress")]
    public async Task Lecturas_NoCompletanNiMutanElPrograma(string scope)
    {
        var response = await Read(scope);
        Assert.Contains(response.Status, new[] { "ok", "empty" });
        await programs.DidNotReceive().CompleteTaskAsync(Arg.Any<CompleteTaskInput>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain(programs.ReceivedCalls(), c => !c.GetMethodInfo().Name.StartsWith("Get", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Podcast_MinimizaDatosYEntregaNavegacionSinUrl()
    {
        var result = await Read("podcast");
        Assert.Equal("ok", result.Status); Assert.Equal(Today, result.LocalDate);
        Assert.Equal(mediaId, result.Data!.Value.GetProperty("mediaId").GetGuid());
        var wire = JsonSerializer.Serialize(result, VitaVersions.JsonOptions);
        Assert.DoesNotContain("storage", wire); Assert.DoesNotContain(patient.Id.ToString(), wire);
        var action = Assert.Single(result.Actions);
        Assert.Equal("navigate", action.Type); Assert.Equal("podcast", action.Destination);
        Assert.Equal(result.ResourceVersion, action.ResourceVersion);
    }

    [Fact]
    public async Task Descanso_ExplicitoSinRutinaNiAccionDeCompletar()
    {
        programs.GetSnapshotAsync(enrollmentId, Today, Arg.Any<CancellationToken>()).Returns(Snapshot(true));
        var result = await Read("exercise");
        Assert.Equal("ok", result.Status); Assert.True(result.Data!.Value.GetProperty("rest").GetBoolean());
        Assert.Empty(result.Actions);
    }

    [Fact]
    public async Task RevisionObsoleta_OcultaDatosYAcciones()
    {
        var first = await Read("podcast");
        var same = await Read("podcast", first.ResourceVersion);
        Assert.Equal("ok", same.Status);
        var stale = await Read("podcast", new string('0', 64));
        Assert.Equal("stale", stale.Status); Assert.Null(stale.Data); Assert.Empty(stale.Actions);
    }

    [Fact]
    public async Task SemanaFutura_NoSeActivaNiSePresentaComoHoy()
    {
        var snapshot = Snapshot();
        programs.GetSnapshotAsync(enrollmentId, Today, Arg.Any<CancellationToken>()).Returns(
            snapshot with { Template = snapshot.Template with { CurrentWeekStatus = ProgramWeekStatus.Locked } });
        Assert.Equal("empty", (await Read("day")).Status);
    }

    [Fact]
    public async Task SinPlan_ConservaEstadoPendienteSinExponerBorrador()
    {
        var result = await Read("nutrition");
        Assert.Equal("empty", result.Status);
        Assert.Equal("pending_review", result.Data!.Value.GetProperty("availability").GetString());
        Assert.Empty(result.Actions);
    }

    [Fact]
    public async Task SinTests_NoAutoasignaYNoInventaEvaluaciones()
    {
        Assert.Equal("empty", (await Read("assessments")).Status);
        Assert.Single(tests.ReceivedCalls());
        Assert.Equal("ListAssignmentsByPatientAsync", tests.ReceivedCalls().Single().GetMethodInfo().Name);
    }

    [Fact]
    public async Task Tests_FiltraAjenoCanceladoYMinimizaIdentidad()
    {
        var assignment = new HealthTestAssignment { Id = Guid.NewGuid(), PatientId = patient.Id,
            VersionId = Guid.NewGuid(), Status = HealthTestAssignmentStatus.pending, Notes = "Nota privada ERP" };
        tests.ListAssignmentsByPatientAsync(patient.Id, Arg.Any<CancellationToken>()).Returns(new[] {
            assignment, new HealthTestAssignment { Id = Guid.NewGuid(), PatientId = Guid.NewGuid(), Status = HealthTestAssignmentStatus.pending },
            new HealthTestAssignment { Id = Guid.NewGuid(), PatientId = patient.Id, Status = HealthTestAssignmentStatus.cancelled } });
        var result = await Read("assessments");
        Assert.Equal(1, result.Data!.Value.GetProperty("assignments").GetArrayLength());
        Assert.Equal(assignment.Id, Assert.Single(result.Actions).ResourceId);
        Assert.DoesNotContain("privada", result.Data.Value.GetRawText());
        Assert.DoesNotContain("patientId", result.Data.Value.GetRawText());
    }

    [Theory]
    [InlineData("Inactivo", false)]
    [InlineData("Activo", true)]
    public async Task PacienteInactivoOEliminado_Prohibido(string status, bool deleted)
    {
        patient.Status = status; patient.DeletedAt = deleted ? DateTime.UtcNow : null;
        Assert.Equal("forbidden", (await Read("podcast")).Status);
        Assert.Empty(programs.ReceivedCalls());
    }

    [Fact]
    public async Task IdentidadAjena_NoAccedeAInscripcion()
    {
        patient.UserId = Guid.NewGuid();
        var result = await Handler.Handle(new(Guid.NewGuid(), "day"), default);
        Assert.Equal("forbidden", result.Status); Assert.Empty(programs.ReceivedCalls());
    }

    [Theory]
    [InlineData(403, "forbidden")]
    [InlineData(503, "unavailable")]
    public async Task Consentimiento_RevocadoODocumentoNoDisponible(int status, string expected)
    {
        consent.EnsureUserAsync(patient.UserId!.Value, "chat", Arg.Any<CancellationToken>())
            .ThrowsAsync(new AiConsentException("CONSENT_REQUIRED", "chat", status));
        Assert.Equal(expected, (await Read("day")).Status); Assert.Empty(programs.ReceivedCalls());
    }

    [Fact]
    public async Task Timeout_NoEquivaleASinContenido()
    {
        programs.GetSnapshotAsync(enrollmentId, Today, Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException());
        Assert.Equal("unavailable", (await Read("day")).Status);
    }

    [Fact]
    public async Task Cancelacion_SePropagaSinConvertirEnVacio()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read("day", ct: cts.Token));
        Assert.Empty(patients.ReceivedCalls());
    }

    [Fact]
    public async Task TokenDePeticion_LlegaALasFuentesYConsentimiento()
    {
        using var cts = new CancellationTokenSource();
        await Read("day", ct: cts.Token);
        await patients.Received().GetByUserIdAsync(patient.UserId!.Value, cts.Token);
        await consent.Received().EnsureUserAsync(patient.UserId.Value, "chat", cts.Token);
        await programs.Received().GetSnapshotAsync(enrollmentId, Today, cts.Token);
    }

    [Fact]
    public void ScopeDesconocido_NoSeAcepta()
    {
        Assert.False(new GetVitaContextQueryValidator().Validate(new GetVitaContextQuery(patient.UserId!.Value, "admin")).IsValid);
    }

    [Fact]
    public async Task ProgresoWearable_SemanaLocalUltimoValorSinSumarDuplicadosYConProcedencia()
    {
        var observed = new DateTimeOffset(2026, 11, 15, 1, 30, 0, TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        devices.GetDeviceWindowAsync(patient.Id, Arg.Any<DateTime>(), Arg.Any<DateTime>(), cts.Token)
            .Returns(new[] {
                new CoppAddresd.Application.Features.Measurements.Queries.GetMyMeasurements.MeasurementItemDto(Guid.NewGuid(), "step_count", "Pasos", 1000, "count", "pasos", observed.AddHours(-1), "device"),
                new CoppAddresd.Application.Features.Measurements.Queries.GetMyMeasurements.MeasurementItemDto(Guid.NewGuid(), "step_count", "Pasos", 2000, "count", "pasos", observed, "device"),
                new CoppAddresd.Application.Features.Measurements.Queries.GetMyMeasurements.MeasurementItemDto(Guid.NewGuid(), "sleep_minutes", "Sueño", 420, "min", "min", observed, "device"),
                new CoppAddresd.Application.Features.Measurements.Queries.GetMyMeasurements.MeasurementItemDto(Guid.NewGuid(), "activity_kcal", "Actividad", 150, "kcal", "kcal", observed, "device") });
        var result = await Read("progress", ct: cts.Token);
        var data = result.Data!.Value;
        Assert.Equal("2026-11-09", data.GetProperty("fromLocalDate").GetString());
        var metrics = data.GetProperty("deviceMetrics").EnumerateArray().ToArray();
        Assert.Equal(3, metrics.Length);
        Assert.Equal(2000, metrics.Single(m => m.GetProperty("metricCode").GetString() == "step_count").GetProperty("value").GetInt32());
        Assert.All(metrics, m => { Assert.Equal("2026-11-14", m.GetProperty("localDate").GetString()); Assert.Equal("device", m.GetProperty("source").GetString()); });
        await devices.Received(1).GetDeviceWindowAsync(patient.Id, new DateTime(2026, 11, 9, 5, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 15, 2, 0, 0, DateTimeKind.Utc), cts.Token);
    }
}
