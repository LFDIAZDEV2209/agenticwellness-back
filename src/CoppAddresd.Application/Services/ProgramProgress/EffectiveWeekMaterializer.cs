using System.Text.Json;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Domain.Entities.ProgramProgress;

namespace CoppAddresd.Application.Services.ProgramProgress;

public sealed record EffectiveWeekMaterialization(JsonElement Snapshot, int Gaps, int ReplacedProgramItems);

/// <summary>Completa referencias de programa al activar; nunca pisa decisiones individuales ni copia el legado.</summary>
public static class EffectiveWeekMaterializer
{
    public static EffectiveWeekMaterialization Build(EffectiveContentData data, ProgramEnrollment enrollment,
        ProgramWeek week, IEffectiveDayContentResolver resolver, CancellationToken ct)
    {
        var rows = WeekSnapshotJson.Parse(week.TasksSnapshot).ToList();
        var scoped = data with { Weeks = data.Weeks.Where(w => w.Id != week.Id).Append(week).ToArray() };
        var days = resolver.ResolveRange(scoped, enrollment, week.WeekStartDateLocal, week.WeekEndDateLocal, ct);
        var changed = 0;
        foreach (var day in days)
            for (var index = 0; index < rows.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var item = rows[index];
                if (item.Weekday != day.Weekday || item.Origin != "program") continue;
                var updated = item.TaskCode switch
                {
                    "podcast" when !day.Podcast.Missing => item with { MediaId = day.Podcast.Id },
                    "ejercicio" when !day.Routine.Missing && day.Routine.Source != "legacy" => item with { RoutineId = day.Routine.Id, Rest = day.Routine.Rest ? true : item.Rest },
                    "nut" when day.Nutrition.Source == "engine" => item with { NutritionPlanId = day.Nutrition.Id },
                    _ => item,
                };
                if (updated != item) { rows[index] = updated; changed++; }
            }
        return new(WeekSnapshotJson.Serialize(rows), days.Count(d => d.HasGaps), changed);
    }
}
