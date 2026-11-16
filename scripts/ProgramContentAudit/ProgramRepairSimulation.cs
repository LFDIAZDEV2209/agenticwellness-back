using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Simulación administrativa 1.14 sobre el resolvedor 1.9 real. No elige un
/// reemplazo editorial ni cambia la precedencia §5.1 para hacer pasar el cero.
/// </summary>
internal static class ProgramRepairSimulation
{
    private static readonly Guid ProtectedPatient = Guid.Parse("0c487f75-a462-4fda-894d-61c031441884");

    public static async Task<object> RunAsync(AppDbContext db, EffectiveContentData data,
        IEffectiveDayContentResolver resolver, int canonicalTotal, CancellationToken ct)
    {
        // Solo PostgreSQL local; el llamador ya abrió REPEATABLE READ / READ ONLY.
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        if (connection.Host is not ("127.0.0.1" or "localhost"))
            throw new InvalidOperationException("La simulación 1.14 está limitada a la BD local.");
        var readOnly = (await db.Database.SqlQueryRaw<string>("SHOW transaction_read_only").ToArrayAsync(ct)).Single();
        var isolation = (await db.Database.SqlQueryRaw<string>("SHOW transaction_isolation").ToArrayAsync(ct)).Single();
        if (readOnly != "on" || isolation != "repeatable read")
            throw new InvalidOperationException("La simulación exige READ ONLY / REPEATABLE READ.");
        var history = (await db.ProgramWeeks.AsNoTracking()
            .Where(w => w.DailyCheckIns.Any() || w.TaskCompletions.Any()).Select(w => w.Id).ToArrayAsync(ct)).ToHashSet();
        var captured = DateTimeOffset.UtcNow;
        var counts = new Dictionary<string, int> { ["podcast"] = 0, ["ejercicio"] = 0, ["nut"] = 0 };
        var evaluated = 0; var gapDays = 0; var protectedGaps = 0; var protectedDays = 0;
        var eligibleFutureGaps = 0; var startedClosedOrHistoryGaps = 0; var individualGaps = 0;
        var hypotheticalProgramInsertions = 0; var hypotheticalGapsFixed = 0;
        var eligibleWeeks = 0; var missingWeeks = 0;
        var affected = new HashSet<Guid>();
        var eligibleCatalogPodcast = data.PublishedPodcasts.Order().FirstOrDefault();
        foreach (var enrollment in data.Enrollments)
        {
            ct.ThrowIfCancellationRequested();
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(captured.UtcDateTime,
                TimeZoneInfo.FindSystemTimeZoneById(enrollment.Timezone)));
            var last = enrollment.StartLocalDate.AddDays(EffectiveDayContentResolver.DurationDays(enrollment.Template!) - 1);
            var days = resolver.ResolveRange(data, enrollment, today, last, ct);
            evaluated += days.Count;
            var gaps = days.Where(d => d.HasGaps).ToArray();
            gapDays += gaps.Length;
            foreach (var day in gaps)
            {
                if (day.Podcast.Missing) counts["podcast"]++;
                if (day.Routine.Missing) counts["ejercicio"]++;
                if (day.Nutrition.Missing) counts["nut"]++;
                affected.Add(day.PatientId);
            }
            if (enrollment.PatientId == ProtectedPatient)
            {
                protectedGaps += gaps.Length; protectedDays += days.Count;
                continue;
            }
            foreach (var group in gaps.GroupBy(d => d.WeekNumber))
            {
                ct.ThrowIfCancellationRequested();
                var week = data.Weeks.SingleOrDefault(w => w.EnrollmentId == enrollment.Id && w.WeekNumber == group.Key);
                if (week is null) { missingWeeks++; startedClosedOrHistoryGaps += group.Count(); continue; }
                var derivedStart = enrollment.StartLocalDate.AddDays((group.Key - 1) * 7);
                if (week.Status != ProgramWeekStatus.Locked || week.ActivatedAt.HasValue || week.CompletedAt.HasValue
                    || week.WeekStartDateLocal <= today || derivedStart <= today || history.Contains(week.Id))
                { startedClosedOrHistoryGaps += group.Count(); continue; }
                eligibleWeeks++;
                var original = WeekSnapshotJson.Parse(week.TasksSnapshot);
                var altered = original.ToList();
                var candidates = new List<EffectiveDayContent>();
                foreach (var day in group)
                {
                    var existing = original.Where(t => t.Weekday == day.Weekday && t.TaskCode == "podcast").ToArray();
                    if (existing.Any(t => t.Origin != "program")) { individualGaps++; continue; }
                    eligibleFutureGaps++;
                    if (!day.Podcast.Missing || eligibleCatalogPodcast == Guid.Empty) continue;
                    // CONTRAFACTUAL, solo en memoria: incluso dar un podcast válido
                    // arbitrario a program futuro no lo hace fuente en §5.1.
                    // No es una propuesta de reemplazo ni un candidato para aplicar.
                    altered.RemoveAll(t => t.Weekday == day.Weekday && t.TaskCode == "podcast" && t.Origin == "program");
                    altered.Add(new WeekSnapshotTaskRow(day.Weekday, "podcast", 0, 0,
                        MediaId: eligibleCatalogPodcast, Origin: "program"));
                    candidates.Add(day); hypotheticalProgramInsertions++;
                }
                if (candidates.Count == 0) continue;
                var diagnosticWeek = new ProgramWeek
                {
                    Id = week.Id, EnrollmentId = week.EnrollmentId, WeekNumber = week.WeekNumber, Status = week.Status,
                    WeekStartDateLocal = week.WeekStartDateLocal, WeekEndDateLocal = week.WeekEndDateLocal,
                    TasksSnapshot = WeekSnapshotJson.Serialize(altered), ActivatedAt = week.ActivatedAt, CompletedAt = week.CompletedAt,
                };
                var diagnostic = data with { Weeks = [.. data.Weeks.Where(w => w.EnrollmentId == enrollment.Id && w.Id != week.Id), diagnosticWeek] };
                foreach (var before in candidates)
                    if (!resolver.Resolve(diagnostic, enrollment, before.Date, ct).HasGaps) hypotheticalGapsFixed++;
            }
        }
        if (gapDays != canonicalTotal) throw new InvalidOperationException("La simulación no coincide con la consulta 1.10b.");
        if (counts["ejercicio"] != 0 || counts["nut"] != 0 || hypotheticalGapsFixed != 0)
            throw new InvalidOperationException("El estado cambió respecto del caso diagnosticado: revisar fuentes antes de decidir una reparación.");
        return new
        {
            operation = "1.14-simulate", baseCommit = "e0a9c29", capturedAt = captured,
            database = connection.Database, transactionReadOnly = readOnly, isolation,
            activeEnrollments = data.Enrollments.Count, evaluatedDays = evaluated, canonicalGapDays = gapDays,
            affectedPatients = affected.Count, gapsByClass = counts,
            protectedFixtureEvaluatedDays = protectedDays, protectedFixtureGapDays = protectedGaps,
            excludedProtectedFixture = ProtectedPatient,
            futureLockedWeeksWithGaps = eligibleWeeks, futureEligibleGapDays = eligibleFutureGaps,
            excludedStartedClosedOrHistoryGapDays = startedClosedOrHistoryGaps,
            preservedIndividualGapDays = individualGaps, nonMaterializedGapWeeks = missingWeeks,
            counterfactualProgramCellsInMemory = hypotheticalProgramInsertions,
            counterfactualCanonicalDaysRepaired = hypotheticalGapsFixed,
            plannedDatabaseUpdates = 0, canonicalDaysRepairableUnderCurrentRule = 0,
            sharedDatabaseWrites = 0, canReachGlobalZero = gapDays == 0,
            applyStatus = "blocked-no-effective-source", reverseStatus = "not-exercised-no-changes-applied",
            reason = "Podcast: individual → plantilla semanal → genérica. Sin fuente válida es hueco. program futuro no tiene precedencia; no se cambia individual, plantilla, catálogo, estado ni fixture protegida para fabricar cero.",
            diagnosticOnly = true,
        };
    }
}
