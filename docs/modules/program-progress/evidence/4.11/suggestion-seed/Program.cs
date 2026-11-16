using System.Text.Json;
using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.LegalDocuments;
using CoppAddresd.Application.Features.ProgramProgress.WeeklySuggestions;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using CoppAddresd.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

// Preparación sintética explícita: proveedor HTTP Postman, handler/guard/store EF reales.
// Única mutación de negocio esperada: PatientAiBrief del paciente QA nuevo indicado.
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2)); var ct = deadline.Token;
    using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(args[0], ct));
    var root = metadata.RootElement;
    var patient = root.GetProperty("patientId").GetGuid();
    if (patient != Guid.Parse("5e3a337e-b29a-4c10-a8a8-904e55bd09e1"))
        throw new InvalidOperationException("Solo paciente QA propio de esta ejecución.");
    var enrollment = root.GetProperty("enrollmentId").GetGuid(); var week = root.GetProperty("weekNumber").GetInt32();
    var account = root.GetProperty("accounts").GetProperty("generator");
    var user = account.GetProperty("userId").GetGuid();
    var permissions = account.GetProperty("permissions").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
    var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("COP_TEST_DB_CONNECTION")
        ?? throw new InvalidOperationException("Falta conexión local en memoria."));
    if (connection.Host is not ("127.0.0.1" or "localhost")) throw new InvalidOperationException("Solo PostgreSQL local.");
    connection.ApplicationName = "codex-back-4.11-synthetic-owned-brief";
    await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection.ConnectionString).Options);
    var actor = new SeedActor(db, user, permissions);
    var scope = new PatientScopeGuard(actor, new PatientScopeStore(db));
    if (await WeeklySuggestionsAccess.EnsureAsync(scope, enrollment, true, ct) != patient) throw new InvalidOperationException("Inscripción ajena.");
    var configuration = new ConfigurationBuilder().AddJsonFile(args[2], false).AddJsonFile(args[3], true).Build();
    var services = new ServiceCollection(); services.AddOptions<LegalConsentSettings>();
    await using var provider = services.BuildServiceProvider();
    var consent = new LegalConsentService(db, provider.GetRequiredService<IOptionsMonitor<LegalConsentSettings>>(), TimeProvider.System);
    var clinical = new ClinicalContextService(new PatientRepository(db), new ClinicalMeasurementRepository(db), NullLogger<ClinicalContextService>.Instance);
    var safety = new SafetyRulesService(new SafetyRuleRepository(db), NullLogger<SafetyRulesService>.Instance);
    var effective = new EffectiveDayContentStore(db);
    var store = new WeeklySuggestionsStore(db, effective, new EffectiveDayContentResolver(effective), new PatientSummaryStore(db),
        clinical, safety, new ProgramRepository(db, configuration, clock: TimeProvider.System), scope, consent, configuration, TimeProvider.System);
    var before = await db.ProgramWeeks.AsNoTracking().Where(w => w.EnrollmentId == enrollment && w.WeekNumber == week)
        .Select(w => w.TasksSnapshot).SingleAsync(ct);
    var rowsBefore = await db.PatientAiBriefs.AsNoTracking().CountAsync(b => b.PatientId == patient, ct);
    var input = await store.CaptureAsync(enrollment, week, "es", ct);
    if (input.Input.Facts.Count == 0 || input.Targets.Count == 0) throw new InvalidOperationException("Sin hechos/opciones reales.");
    using var http = new HttpClient(); http.DefaultRequestHeaders.Add("X-Mock-Slug", "weekly");
    var client = new WeeklySuggestionsClient(http, Options.Create(new AiServiceSettings
        { BaseUrl = "http://127.0.0.1:4533", InternalApiKey = "simulation-only-key", TimeoutSeconds = 5 }));
    var handler = new GenerateWeeklySuggestionsHandler(scope, consent, store, client, NullLogger<GenerateWeeklySuggestionsHandler>.Instance);
    var result = await handler.Handle(new(enrollment, week, "es"), ct);
    if (result.Status != "ok" || result.Draft is not { Suggestions.Count: > 0 } draft
        || draft.Model != "postman-weekly-synthetic" || draft.State != "draft"
        || draft.Suggestions.Any(s => s.AppliedAt != null || s.AppliedBy != null || s.State != "draft"))
        throw new InvalidOperationException("No se obtuvo el borrador sintético esperado.");
    var after = await db.ProgramWeeks.AsNoTracking().Where(w => w.EnrollmentId == enrollment && w.WeekNumber == week)
        .Select(w => w.TasksSnapshot).SingleAsync(ct);
    if (before.GetRawText() != after.GetRawText()) throw new InvalidOperationException("La generación alteró el plan.");
    if (await db.PatientAiBriefs.AsNoTracking().CountAsync(b => b.PatientId == patient, ct) != rowsBefore + 1)
        throw new InvalidOperationException("Cantidad de borradores inesperada.");
    var output = new { source = "Postman HTTP simulado; handler y EF reales; no salida LLM live", commit = "e0a9c29",
        patientId = patient, enrollmentId = enrollment, weekNumber = week, status = result.Status,
        briefId = draft.Id, model = draft.Model, suggestionCount = draft.Suggestions.Count,
        draft.State, noApplyCalled = true, weekSnapshotUnchanged = true, newBriefRows = 1 };
    await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(output, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), ct);
    Console.WriteLine($"Borrador QA sintético listo: {draft.Suggestions.Count} sugerencias; snapshot sin cambios; sin aplicar.");
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Preparación QA interrumpida: {exception.GetType().Name}; detalle omitido.");
    Environment.ExitCode = 1;
}

sealed class SeedActor(AppDbContext db, Guid user, IReadOnlySet<string> permissions) : IPatientScopeActor
{
    public Guid? UserId => user;
    public bool IsAdmin => false;
    public Task<Guid?> ProfessionalIdAsync(CancellationToken ct) => db.Professionals.AsNoTracking()
        .Where(p => p.Employee!.UserId == user).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct);
    public Task<bool> HasPermissionAsync(string permission, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(permissions.Contains(permission)); }
}
