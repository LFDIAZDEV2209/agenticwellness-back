using CoppAddresd.Application.Features.Nutrition.Commands;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed class NutritionCycleReviewStore(AppDbContext db, IProgramRepository programs) : INutritionCycleReviewStore
{
    public Task<T> WithPatientLockAsync<T>(Guid patientId, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
        => db.Database.CreateExecutionStrategy().ExecuteAsync(async writeCt =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(writeCt);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM app.patient_profiles WHERE id = {patientId} FOR UPDATE", writeCt);
            try { var result = await operation(writeCt); await tx.CommitAsync(writeCt); return result; }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); throw new BusinessRuleViolationException("La revisión cambió; vuelve a cargarla."); }
            catch { db.ChangeTracker.Clear(); throw; }
        }, ct);

    public Task<NutritionCycleReview?> LoadAsync(Guid cycleId, CancellationToken ct)
        => db.Set<NutritionCycleReview>().Include(r => r.Attachments).SingleOrDefaultAsync(r => r.CycleId == cycleId, ct);
    public async Task SaveAsync(NutritionCycleReview review, CancellationToken ct)
    {
        if (db.Entry(review).State == EntityState.Detached) db.Set<NutritionCycleReview>().Add(review);
        await db.SaveChangesAsync(ct);
    }
    public async Task<string> AuthorNameAsync(Guid userId, CancellationToken ct)
        => (await db.Employees.AsNoTracking().Where(e => e.UserId == userId)
            .OrderBy(e => e.DeletedAt != null).ThenBy(e => e.Id).Select(e => e.FirstName + " " + e.LastName).FirstOrDefaultAsync(ct))?.Trim() ?? "";

    public async Task<CycleIntakeAverages> AveragesAsync(Guid patientId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var days = db.NutritionIntakeLogs.AsNoTracking().Where(l => l.PatientId == patientId && l.LocalDate >= from && l.LocalDate <= to)
            .GroupBy(l => l.LocalDate).Select(g => new
            {
                Calories = g.Any(l => l.MealCode != MealCode.agua && l.Calories != null) ? g.Where(l => l.MealCode != MealCode.agua).Sum(l => l.Calories) : null,
                Protein = g.Any(l => l.MealCode != MealCode.agua && l.ProteinG != null) ? g.Where(l => l.MealCode != MealCode.agua).Sum(l => l.ProteinG) : null,
                Carbs = g.Any(l => l.MealCode != MealCode.agua && l.CarbsG != null) ? g.Where(l => l.MealCode != MealCode.agua).Sum(l => l.CarbsG) : null,
                Fat = g.Any(l => l.MealCode != MealCode.agua && l.FatG != null) ? g.Where(l => l.MealCode != MealCode.agua).Sum(l => l.FatG) : null,
                Fiber = g.Any(l => l.MealCode != MealCode.agua && l.FiberG != null) ? g.Where(l => l.MealCode != MealCode.agua).Sum(l => l.FiberG) : null,
                Water = g.Any(l => l.WaterMl != null) ? g.Sum(l => l.WaterMl) : null
            });
        var averages = await days.GroupBy(_ => 1).Select(g => new CycleIntakeAverages(g.Average(l => l.Calories),
            g.Average(l => l.Protein), g.Average(l => l.Carbs), g.Average(l => l.Fat), g.Average(l => l.Fiber), g.Average(l => (decimal?)l.Water))).SingleOrDefaultAsync(ct);
        return averages ?? new(null, null, null, null, null, null);
    }
    public Task<NutritionPrescription?> PrescriptionAsync(Guid id, CancellationToken ct)
        => db.NutritionPrescriptions.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
    public async Task MarkCycleReviewedAsync(Guid cycleId, CancellationToken ct)
        => await db.NutritionCycles.Where(c => c.Id == cycleId && c.State != "interrumpido").ExecuteUpdateAsync(s => s.SetProperty(c => c.State, "revisado"), ct);
    public Task AuditAsync(NutritionCycleReview review, Guid actorId, string action, CancellationToken ct)
        => programs.WriteAuditRowAsync("UPDATE", "app", "nutrition_cycle_reviews", review.Id, actorId, ct,
            metadata: System.Text.Json.JsonSerializer.SerializeToElement(new { @event = action, cycleId = review.CycleId, revision = review.Revision }));

    public async Task NotifyPublishedAsync(Guid patientId, Guid cycleId, DateTime now, CancellationToken ct)
    {
        var key = $"nutrition-cycle:{cycleId:D}:review-published";
        if (await db.NotificationDedupeKeys.AnyAsync(k => k.DedupeKey == key, ct)) return;
        var user = await db.PatientProfiles.Where(p => p.Id == patientId).Select(p => p.UserId).SingleAsync(ct);
        if (user is null) return;
        db.NotificationDedupeKeys.Add(new() { Id = Guid.NewGuid(), UserId = user.Value, DedupeKey = key, CreatedAt = new(now, TimeSpan.Zero) });
        db.AppNotifications.Add(new AppNotification { Id = Guid.NewGuid(), PatientId = patientId, Type = "nutrition_cycle_review",
            Title = "Tu nutricionista revisó tu mes", Message = "Ya puedes leer sus recomendaciones en tu plan de nutrición.", Channel = "in_app", SentAt = now });
        await db.SaveChangesAsync(ct);
    }
    public Task<ReviewAttachmentFile?> AttachmentAsync(Guid id, CancellationToken ct)
        => (from a in db.Set<NutritionCycleAttachment>().AsNoTracking()
            join r in db.Set<NutritionCycleReview>() on a.ReviewId equals r.Id
            join c in db.NutritionCycles on r.CycleId equals c.Id
            where a.Id == id select new ReviewAttachmentFile(c.PatientId, a)).SingleOrDefaultAsync(ct);
    public async Task DeleteAttachmentAsync(NutritionCycleAttachment attachment, CancellationToken ct)
    { db.Set<NutritionCycleAttachment>().Remove(attachment); await db.SaveChangesAsync(ct); }
    public Task AddAttachmentAsync(NutritionCycleAttachment attachment, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); db.Set<NutritionCycleAttachment>().Add(attachment); return Task.CompletedTask; }
}
