using System.Text.Json;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

internal static class XpRuleTimeline
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static async Task<XpRule?> ResolveAsync(
        AppDbContext db, string code, DateOnly date, TimeZoneInfo timezone, CancellationToken ct, DateOnly? validityDate = null)
    {
        // La primera edición desde el inicio del día conserva la regla que
        // estaba vigente al comenzar ese día. Una edición a las 00:00 exactas
        // tampoco entra en vigor hasta el día siguiente.
        var start = StartOfDayUtc(date, timezone);
        var json = await db.Database.SqlQuery<string>($"""
            SELECT COALESCE((
                SELECT previous.snapshot
                FROM app.xp_rule_revisions AS previous
                WHERE previous.code = rule.code AND previous.superseded_at >= {start}
                ORDER BY previous.superseded_at, previous.id
                LIMIT 1
            ), to_jsonb(rule))::text AS "Value"
            FROM app.xp_rules AS rule WHERE rule.code = {code}
            """).SingleOrDefaultAsync(ct);

        var rule = json is null ? null : JsonSerializer.Deserialize<XpRule>(json, Json);
        var inWindow = validityDate ?? date;
        return rule is { Active: true } && rule.ValidFrom <= inWindow
            && (rule.ValidUntil is null || rule.ValidUntil >= inWindow) ? rule : null;
    }

    public static DateTime StartOfDayUtc(DateOnly date, TimeZoneInfo timezone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Algunas zonas adelantan el reloj justo a medianoche. En un solape,
        // el primer instante de medianoche (offset mayor) inicia el día.
        while (timezone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return timezone.IsAmbiguousTime(local)
            ? new DateTimeOffset(local, timezone.GetAmbiguousTimeOffsets(local).Max()).UtcDateTime
            : TimeZoneInfo.ConvertTimeToUtc(local, timezone);
    }
}
