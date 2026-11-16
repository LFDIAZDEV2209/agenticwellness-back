using System.Text.Json;
using CoppAddresd.Application.Features.ProgramProgress.Queries.Erp;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

// Herramienta administrativa local: solo lectura, sin identificadores ni datos clínicos en salida.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var connection = Environment.GetEnvironmentVariable("COP_PROGRAM_AUDIT_CONNECTION")
        ?? throw new InvalidOperationException("Falta conexión del entorno seguro.");
    var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options;
    await using var db = new AppDbContext(options);
    await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellation.Token);
    await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellation.Token);
    var guard = new PatientScopeGuard(new AdministrativeActor(), new PatientScopeStore(db));
    var store = new EffectiveDayContentStore(db, guard); var resolver = new EffectiveDayContentResolver(store);
    var result = await new GetContentGapsQueryHandler(store, resolver, TimeProvider.System).Handle(new(1, 1), cancellation.Token);
    var data = await store.LoadScopedActiveAsync(null, "Program.View", cancellation.Token);
    if (args is ["--simulate-legacy-repair", var backupPath, var legacyReportPath])
    {
        var report = await ProgramLegacyRepairSimulation.RunAsync(db, data, resolver, result.Total, backupPath, cancellation.Token);
        await File.WriteAllTextAsync(legacyReportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancellation.Token);
        await transaction.RollbackAsync(cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }
    if (args is ["--simulate-repair", var reportPath])
    {
        var report = await ProgramRepairSimulation.RunAsync(db, data, resolver, result.Total, cancellation.Token);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancellation.Token);
        await transaction.RollbackAsync(cancellation.Token);
        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }
    var counts = new Dictionary<string, int> { ["podcast"] = 0, ["ejercicio"] = 0, ["nut"] = 0 };
    var templates = new Dictionary<string, (int Enrollments, int Days, int Gaps)>();
    var protectedGaps = 0;
    var now = DateTime.UtcNow;
    foreach (var enrollment in data.Enrollments)
    {
        var key = enrollment.Template!.Code;
        var group = templates.GetValueOrDefault(key);
        group.Enrollments++;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById(enrollment.Timezone)));
        var last = enrollment.StartLocalDate.AddDays(EffectiveDayContentResolver.DurationDays(enrollment.Template!) - 1);
        foreach (var day in resolver.ResolveRange(data, enrollment, today, last, cancellation.Token))
        {
            group.Days++;
            if (day.HasGaps) group.Gaps++;
            if (day.HasGaps && enrollment.PatientId == Guid.Parse("0c487f75-a462-4fda-894d-61c031441884")) protectedGaps++;
            if (day.Podcast.Missing) counts["podcast"]++;
            if (day.Routine.Missing) counts["ejercicio"]++;
            if (day.Nutrition.Missing) counts["nut"]++;
        }
        templates[key] = group;
    }
    Console.WriteLine(JsonSerializer.Serialize(new { consulta = "1.10b", capturedAt = DateTimeOffset.UtcNow,
        totalDiasConHuecos = result.Total, clases = counts, escrituras = 0, protectedGaps,
        templates = templates.Select(t => new { code = t.Key, enrollments = t.Value.Enrollments, days = t.Value.Days, gaps = t.Value.Gaps }) }));
    await transaction.RollbackAsync(cancellation.Token);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("Auditoría interrumpida: " + error.GetType().Name + (error is Npgsql.PostgresException pg ? " " + pg.SqlState : ""));
    return 1;
}

sealed class AdministrativeActor : IPatientScopeActor
{
    // Identidad efímera de la operación administrativa; no crea ni suplanta usuarios persistidos.
    public Guid? UserId { get; } = Guid.NewGuid();
    public bool IsAdmin => true;
    public Task<Guid?> ProfessionalIdAsync(CancellationToken ct) => Task.FromResult<Guid?>(null);
    public Task<bool> HasPermissionAsync(string permission, CancellationToken ct) => Task.FromResult(permission == "Program.View");
}
