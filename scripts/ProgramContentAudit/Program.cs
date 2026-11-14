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
    var counts = new Dictionary<string, int> { ["podcast"] = 0, ["ejercicio"] = 0, ["nut"] = 0 };
    var now = DateTime.UtcNow;
    foreach (var enrollment in data.Enrollments)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById(enrollment.Timezone)));
        var last = enrollment.StartLocalDate.AddDays(EffectiveDayContentResolver.DurationDays(enrollment.Template!) - 1);
        foreach (var day in resolver.ResolveRange(data, enrollment, today, last, cancellation.Token))
        {
            if (day.Podcast.Missing) counts["podcast"]++;
            if (day.Routine.Missing) counts["ejercicio"]++;
            if (day.Nutrition.Missing) counts["nut"]++;
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { consulta = "1.10b", totalDiasConHuecos = result.Total, clases = counts, escrituras = 0 }));
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
