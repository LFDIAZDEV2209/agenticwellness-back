using System.Text.Json;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Contrafactual local de seis referencias editoriales; usa el resolver real sin escribir en BD.</summary>
internal static class ProgramLegacyRepairSimulation
{
    public static async Task<object> RunAsync(AppDbContext db, EffectiveContentData data,
        IEffectiveDayContentResolver resolver, int canonicalTotal, string backupPath, CancellationToken ct)
    {
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        if (connection.Host is not ("127.0.0.1" or "localhost")) throw new InvalidOperationException("Solo BD local.");
        if ((await db.Database.SqlQueryRaw<string>("SHOW transaction_read_only").ToArrayAsync(ct)).Single() != "on")
            throw new InvalidOperationException("La simulación exige READ ONLY.");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(backupPath, ct));
        var root = json.RootElement;
        if (root.GetProperty("operation").GetString() != "1.14-legacy-refs") throw new InvalidOperationException("Respaldo incorrecto.");
        var templateId = root.GetProperty("templateId").GetGuid();
        var beforeRefs = root.GetProperty("beforeRows").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetGuid(), r => r.GetProperty("media_id").GetGuid());
        var afterRefs = root.GetProperty("afterRows").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetGuid(), r => r.GetProperty("media_id").GetGuid());
        if (beforeRefs.Count != 7 || afterRefs.Count != 7 || beforeRefs.Count(r => afterRefs[r.Key] != r.Value) != 6
            || afterRefs.Values.Any(id => !data.PublishedPodcasts.Contains(id)))
            throw new InvalidOperationException("Se esperan seis reemplazos publicados, preservando el séptimo.");
        var captured = DateTimeOffset.UtcNow;
        var before = new Dictionary<Guid, IReadOnlyList<EffectiveDayContent>>();
        var frozen = new List<(ProgramEnrollment Enrollment, EffectiveDayContent Day, bool Closed)>();
        var target = data.Enrollments.Where(e => e.TemplateId == templateId).ToArray();
        var targetIds = target.Select(e => e.Id).ToArray();
        var completions = await db.TaskCompletions.AsNoTracking().CountAsync(c => targetIds.Contains(c.EnrollmentId), ct);
        var checkins = await db.DailyCheckIns.AsNoTracking().CountAsync(c => targetIds.Contains(c.EnrollmentId), ct);
        foreach (var e in data.Enrollments)
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(captured.UtcDateTime, TimeZoneInfo.FindSystemTimeZoneById(e.Timezone)));
            before[e.Id] = resolver.ResolveRange(data, e, today, e.StartLocalDate.AddDays(EffectiveDayContentResolver.DurationDays(e.Template!) - 1), ct);
            if (e.TemplateId != templateId) continue;
            if (e.Template!.Code != "default-83w" || e.Template.TotalWeeks != 83 || e.Template.Status != TemplateStatus.Archived)
                throw new InvalidOperationException("No corresponde a la plantilla archivada de 83 semanas.");
            foreach (var w in data.Weeks.Where(w => w.EnrollmentId == e.Id && (w.Status != ProgramWeekStatus.Locked || w.ActivatedAt.HasValue || w.CompletedAt.HasValue)))
                foreach (var day in resolver.ResolveRange(data, e, w.WeekStartDateLocal, w.WeekEndDateLocal, ct))
                    frozen.Add((e, day, w.Status == ProgramWeekStatus.Completed || w.CompletedAt.HasValue));
        }
        var totalBefore = before.Values.SelectMany(x => x).Count(d => d.HasGaps);
        if (totalBefore != canonicalTotal) throw new InvalidOperationException("La simulación difiere de 1.10b.");
        // AsNoTracking puede producir varias instancias de una plantilla: actualizar todas, solo en memoria.
        foreach (var e in target)
            foreach (var row in e.Template!.DayTemplates.Where(r => beforeRefs.ContainsKey(r.Id)))
            {
                if (row.MediaId != beforeRefs[row.Id] && row.MediaId != afterRefs[row.Id]) throw new InvalidOperationException("CAS de referencia falló.");
                row.MediaId = afterRefs[row.Id];
            }
        var totalAfter = 0; var repaired = 0; var validPodcastChanges = 0; var otherContentChanges = 0;
        var changedOtherTemplateDays = 0; var affected = 0;
        foreach (var e in data.Enrollments)
        {
            var rows = before[e.Id]; var repairedPatient = false;
            foreach (var previous in rows)
            {
                var next = resolver.Resolve(data, e, previous.Date, ct);
                if (next.HasGaps) totalAfter++;
                if (previous.HasGaps && !next.HasGaps) { repaired++; repairedPatient = true; }
                if (!previous.Podcast.Missing && previous.Podcast != next.Podcast) validPodcastChanges++;
                if (previous.Nutrition != next.Nutrition || previous.Routine != next.Routine) otherContentChanges++;
                if (e.TemplateId != templateId && previous != next) changedOtherTemplateDays++;
            }
            if (repairedPatient) affected++;
        }
        var frozenValidChanges = 0; var frozenFallbacksAdded = 0; var closedChanges = 0;
        foreach (var (e, previous, closed) in frozen)
        {
            var next = resolver.Resolve(data, e, previous.Date, ct);
            if (!previous.Podcast.Missing && previous.Podcast != next.Podcast) frozenValidChanges++;
            if (previous.Podcast.Missing && !next.Podcast.Missing) frozenFallbacksAdded++;
            if (closed && previous != next) closedChanges++;
        }
        if (validPodcastChanges != 0 || otherContentChanges != 0 || changedOtherTemplateDays != 0 || frozenValidChanges != 0 || closedChanges != 0
            || completions != 0 || checkins != 0)
            throw new InvalidOperationException("Cambió una protección o apareció historial cerrado: revisar sin aplicar.");
        return new { operation = "1.14-legacy-refs-simulation", capturedAt = captured, readOnly = true,
            proposalSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(backupPath, ct))),
            targetTemplate = "default-83w", activeTargetEnrollments = target.Length,
            canonicalBefore = totalBefore, canonicalAfter = totalAfter, futureDaysRepaired = repaired,
            affectedEnrollments = affected, referenceUpdates = 6, enrollmentMigrations = 0, snapshotsUpdated = 0,
            validPodcastChanges, otherContentChanges, changedOtherTemplateDays, frozenValidChanges,
            startedWeekDaysEvaluated = frozen.Count, startedMissingFallbacksNowAvailable = frozenFallbacksAdded,
            closedChanges, targetTaskCompletions = completions, targetCheckins = checkins,
            databaseWrites = 0, canReachGlobalZero = totalAfter == 0 };
    }
}
