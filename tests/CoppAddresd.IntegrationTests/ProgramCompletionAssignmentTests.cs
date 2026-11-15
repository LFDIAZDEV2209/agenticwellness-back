using CoppAddresd.Application.Features.HealthTests.Events;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities.ProgramProgress;
using CoppAddresd.Domain.Enums.ProgramProgress;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoppAddresd.IntegrationTests;

public sealed partial class ProgramAssignmentIntegrationTests
{
    [AssignmentPostgresFact]
    public async Task ProgramaTerminado_ReplayBateriaYRecuperacionNoCreanOtraInscripcion()
    {
        var seed = await SeedPodcastAssignmentAsync(false);
        await using var db = NewDb();
        var completed = new ProgramEnrollment { Id = Guid.NewGuid(), PatientId = seed.PatientId, TemplateId = seed.TemplateId,
            Status = ProgramEnrollmentStatus.Completed, CurrentWeekNumber = 12, StartLocalDate = new(2026, 8, 24),
            CompletedAt = _lifecycleClock.Current.UtcDateTime };
        db.ProgramEnrollments.Add(completed); await db.SaveChangesAsync();
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPublisher>().Publish(new BatteryCompletedEvent(seed.PatientId, seed.BatteryId));
        var candidates = await scope.ServiceProvider.GetRequiredService<IProgramAssignmentOperationsRepository>()
            .ListRecoveryAsync(_lifecycleClock.Current.UtcDateTime, 5, 100, default);
        Assert.DoesNotContain(candidates, c => c.PatientId == seed.PatientId);
        Assert.Equal(1, await db.ProgramEnrollments.CountAsync(e => e.PatientId == seed.PatientId));
        Assert.False(await db.ProgramAssignmentRecommendations.AnyAsync(r => r.PatientId == seed.PatientId));
        Assert.Equal(ProgramEnrollmentStatus.Completed,
            await db.ProgramEnrollments.Where(e => e.Id == completed.Id).Select(e => e.Status).SingleAsync());
    }
}
