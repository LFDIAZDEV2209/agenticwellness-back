using System.Data;
using System.Text.Json;
using CoppAddresd.Application.Features.ProgramProgress.Queries.Erp;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// Auditoría local: sin host HTTP, jobs, secretos en archivo ni llamadas a otros servicios.
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var ct = cancellation.Token;
var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("COP_TEST_DB_CONNECTION")
    ?? throw new InvalidOperationException("Falta conexión local en memoria."));
if (connection.Host is not ("127.0.0.1" or "localhost")) throw new InvalidOperationException("Solo PostgreSQL local.");
connection.ApplicationName = "codex-back-3.21-readonly";
connection.Options = "-c default_transaction_read_only=on -c statement_timeout=120000 -c lock_timeout=3000";
await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection.ConnectionString).Options);
await db.Database.OpenConnectionAsync(ct);
await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
var readOnly = await db.Database.SqlQueryRaw<string>("SHOW transaction_read_only").ToListAsync(ct);
if (readOnly.Single() != "on") throw new InvalidOperationException("La conexión debe ser solo lectura.");
var applied = await db.Database.SqlQueryRaw<string>("SELECT \"MigrationId\" AS \"Value\" FROM public.\"__EFMigrationsHistory\" ORDER BY \"MigrationId\"").ToArrayAsync(ct);
var requested = new[]
{
    "20261114171403_AddNutritionCycles", "20261114174950_AddNutritionCycleInterruption",
    "20261114184553_AddNutritionIntakeRequests", "20261114193738_AddNutritionIntakeDayReviews",
    "20261114194459_AddNutritionCycleHighlights", "20261114200555_AddNutritionCycleReviews",
};
var actor = new AuditActor();
var store = new EffectiveDayContentStore(db, new PatientScopeGuard(actor, new PatientScopeStore(db)));
var resolver = new EffectiveDayContentResolver(store);
var clock = new AuditClock(DateTimeOffset.UtcNow);
// Handler real actual: misma consulta administrativa 1.10b, sin autenticar una sesión HTTP.
var canonical = await new GetContentGapsQueryHandler(store, resolver, clock).Handle(new(1, 100), ct);
var data = await store.LoadScopedActiveAsync(null, "Program.View", ct);
var days = new List<EffectiveDayContent>();
foreach (var enrollment in data.Enrollments)
{
    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.GetUtcNow().UtcDateTime,
        TimeZoneInfo.FindSystemTimeZoneById(enrollment.Timezone)));
    var end = enrollment.StartLocalDate.AddDays(EffectiveDayContentResolver.DurationDays(enrollment.Template!) - 1);
    days.AddRange(resolver.ResolveRange(data, enrollment, today, end, ct));
}
var continuity = days.Where(d => d.Nutrition.Source == "continuity").ToArray();
var invalidContinuity = continuity.Count(d =>
{
    var prescription = data.Prescriptions.Single(p => p.Id == d.Nutrition.Id);
    return !d.Nutrition.Prescription || d.Date <= prescription.End
        || prescription.PatientId != d.PatientId
        || d.Nutrition.DayNumber != prescription.End.DayNumber - prescription.Start.DayNumber + 1
        || !prescription.MenuDays.Contains(d.Nutrition.DayNumber!.Value);
});
if (invalidContinuity != 0) throw new InvalidOperationException("Continuidad sin menú final propio válido.");
var gaps = days.Where(x => x.HasGaps).ToArray();
var protectedEnrollments = data.Enrollments.Count(e => e.PatientId == Guid.Parse("0c487f75-a462-4fda-894d-61c031441884"));
if (protectedEnrollments == 0) throw new InvalidOperationException("La fixture protegida debe permanecer incluida.");
if (canonical.Total != gaps.Length) throw new InvalidOperationException("El desglose debe coincidir con el handler.");
var catalog = await store.LoadGeneralCatalogAsync(ct);
var result = new
{
    commit = args[1], capturedAt = clock.GetUtcNow(),
    database = connection.Database, transactionReadOnly = readOnly.Single(), isolation = "RepeatableRead",
    scope = "Program.View, actor administrativo de auditoría; guard y store reales, sin sesión HTTP",
    appliedMigrationCount = applied.Length, latestMigration = applied.LastOrDefault(),
    migrations = requested.Select(id => new { id, applied = applied.Contains(id) }),
    unrecognizedMigrations = applied.Except(db.Database.GetMigrations()).ToArray(),
    pendingMigrations = db.Database.GetMigrations().Except(applied).ToArray(),
    activeEnrollments = data.Enrollments.Count,
    evaluatedEnrollments = days.Select(x => x.EnrollmentId).Distinct().Count(), evaluatedDays = days.Count,
    remainingRange = days.Count == 0 ? null : new { from = days.Min(x => x.Date), to = days.Max(x => x.Date) },
    affectedPatients = gaps.Select(x => x.PatientId).Distinct().Count(),
    affectedEnrollments = gaps.Select(x => x.EnrollmentId).Distinct().Count(), totalDaysWithGaps = canonical.Total,
    gapsByClass = new { podcast = days.Count(x => x.Podcast.Missing), ejercicio = days.Count(x => x.Routine.Missing), nut = days.Count(x => x.Nutrition.Missing) },
    nutritionSources = days.GroupBy(x => x.Nutrition.Source).ToDictionary(x => x.Key, x => x.Count()),
    routineSources = days.GroupBy(x => x.Routine.Source).ToDictionary(x => x.Key, x => x.Count()),
    publishedPodcastCount = catalog.PublishedPodcasts.Count,
    continuity = new { days = continuity.Length, enrollments = continuity.Select(d => d.EnrollmentId).Distinct().Count(),
        finalMenuValidated = continuity.Length > 0 && invalidContinuity == 0, invalidDays = invalidContinuity,
        menus = continuity.GroupBy(d => d.Nutrition.DayNumber).Select(g => new { menuDay = g.Key, days = g.Count() }) },
    protectedEnrollments,
    protectedGaps = gaps.Count(d => d.PatientId == Guid.Parse("0c487f75-a462-4fda-894d-61c031441884")),
    templates = data.Enrollments.GroupBy(e => e.Template!.Code).Select(g => {
        var ids = g.Select(e => e.Id).ToHashSet(); var evaluated = days.Where(d => ids.Contains(d.EnrollmentId)).ToArray();
        return new { code = g.Key, enrollments = g.Count(), days = evaluated.Length, gaps = evaluated.Count(d => d.HasGaps),
            podcast = evaluated.Count(d => d.Podcast.Missing), ejercicio = evaluated.Count(d => d.Routine.Missing), nut = evaluated.Count(d => d.Nutrition.Missing) };
    }),
    writes = 0,
    byProgramDuration = days.GroupBy(x => data.Enrollments.Single(e => e.Id == x.EnrollmentId).Template!.TotalWeeks)
        .Select(group => new { totalWeeks = group.Key, days = group.Count(), podcast = group.Count(x => x.Podcast.Missing),
            ejercicio = group.Count(x => x.Routine.Missing), nut = group.Count(x => x.Nutrition.Missing) }),
};
await transaction.RollbackAsync(ct);
await File.WriteAllTextAsync(args[0], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), ct);
Console.WriteLine("Auditoría completada: transacción solo lectura revertida; salida agregada sin identificadores personales.");

sealed class AuditActor : IPatientScopeActor
{
    public Guid? UserId => Guid.Parse("00000000-0000-0000-0000-000000000321");
    public bool IsAdmin => true;
    public Task<Guid?> ProfessionalIdAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult<Guid?>(null); }
    public Task<bool> HasPermissionAsync(string permission, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(permission == "Program.View"); }
}
sealed class AuditClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
