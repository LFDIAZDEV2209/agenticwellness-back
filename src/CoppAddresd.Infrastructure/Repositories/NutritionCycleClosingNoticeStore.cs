using CoppAddresd.Application.Features.Nutrition.Cases;
using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

/// <summary>La clave persistida es el aviso ERP; el texto se proyecta solo después de filtrar su alcance vivo.</summary>
public sealed class NutritionCycleClosingNoticeStore(AppDbContext db, IPatientScopeStore patients) : INutritionCycleClosingNoticeStore
{
    public Task<int> EmitAsync(DateTimeOffset utcNow, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app.notification_dedupe_keys (id, dedupe_key, user_id, created_at)
            SELECT gen_random_uuid(), 'nutrition-cycle:' || c.id::text || ':closing:' || e.user_id::text,
                   e.user_id, {utcNow}
            FROM app.nutrition_cycles c
            JOIN app.patient_profiles p ON p.id = c.patient_id AND p.deleted_at IS NULL
            JOIN pg_timezone_names tz ON tz.name = p.timezone
            JOIN app.patient_professionals pp ON pp.patient_id = c.patient_id AND pp.status = 'Active'
            JOIN erp.professionals pro ON pro.id = pp.professional_id
            JOIN erp.employees e ON e.id = pro.employee_id AND e.user_id IS NOT NULL AND e.deleted_at IS NULL AND e.status = 'Active'
            WHERE c.number IS NOT NULL AND c.state IN ('en_curso','por_revisar') AND c.interrupted_at IS NULL
              AND c.start_date <= timezone(tz.name, {utcNow})::date
              AND c.end_date BETWEEN timezone(tz.name, {utcNow})::date
                                 AND timezone(tz.name, {utcNow})::date + {NutritionStateMachine.RenewalLeadDays}
              AND NOT EXISTS (SELECT 1 FROM app.nutrition_cycles s WHERE s.patient_id = c.patient_id AND s.number > c.number)
            ON CONFLICT (dedupe_key) DO NOTHING
            """, ct);

    public async Task<CycleClosingNoticePage> ListAsync(Guid userId, PatientScopeFilter scope, int page, int pageSize, CancellationToken ct)
    {
        var visible = patients.VisiblePatients(scope.ProfessionalId, scope.IsAdmin);
        var suffix = ":closing:" + userId.ToString("D");
        var query = from c in db.NutritionCycles.AsNoTracking()
            join p in db.PatientProfiles on c.PatientId equals p.Id
            join n in db.NotificationDedupeKeys on "nutrition-cycle:" + c.Id.ToString() + suffix equals n.DedupeKey
            where n.UserId == userId && visible.Contains(c.PatientId) && c.Number != null && c.EndDate != null
                && (c.State == "en_curso" || c.State == "por_revisar") && c.InterruptedAt == null
                && !db.NutritionCycles.Any(s => s.PatientId == c.PatientId && s.Number > c.Number)
            select new { Notice = n, Cycle = c, Name = p.FirstName + " " + p.LastName };
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.Notice.CreatedAt).ThenBy(r => r.Notice.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(r => new { r.Notice.Id, CycleId = r.Cycle.Id, r.Cycle.PatientId, r.Name, r.Cycle.Number,
                r.Cycle.EndDate, r.Notice.CreatedAt }).ToArrayAsync(ct);
        return new(rows.Select(r => new CycleClosingNoticeDto(r.Id, r.CycleId, r.PatientId, r.Name, r.Number!.Value,
            r.EndDate!.Value, r.CreatedAt, "Revisar cierre nutricional", "Review nutrition cycle closing",
            $"Prepara el cierre del ciclo {r.Number} de {r.Name}.", $"Prepare the closing review for {r.Name}'s cycle {r.Number}.")).ToArray(), total, page, pageSize);
    }
}
