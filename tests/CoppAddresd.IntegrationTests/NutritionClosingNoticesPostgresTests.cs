using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Domain.Exceptions;
using CoppAddresd.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task AvisoCierre_VentanaLocal_ProfesionalesAsignados_ConcurrenteSinDuplicar_YBandejaSinAjenos()
    {
        await using var db = Context(); var patient = await PatientAsync(db); var other = await PatientAsync(db);
        var secondUser = await PatientAsync(db); var foreignUser = await PatientAsync(db); var inactiveUser = await PatientAsync(db);
        var plan = await CycleAdherencePlanAsync(db, patient.Id, new(2026, 11, 2), new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc));
        var foreign = await CycleAdherencePlanAsync(db, other.Id, new(2026, 11, 2), new(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc));
        var professional = NutritionProfessional(patient.Id); professional.Employee.UserId = patient.UserId; professional.Employee.Status = "Active";
        var second = NutritionProfessional(patient.Id); second.Employee.UserId = secondUser.UserId; second.Employee.Status = "Active";
        var alien = NutritionProfessional(other.Id); alien.Employee.UserId = foreignUser.UserId; alien.Employee.Status = "Active";
        var inactive = NutritionProfessional(patient.Id); inactive.Employee.UserId = inactiveUser.UserId; inactive.Employee.Status = "Inactive";
        db.Professionals.AddRange(professional, second, alien, inactive);
        db.PatientProfessionalAssignments.AddRange(new() { PatientId = patient.Id, ProfessionalId = professional.Id },
            new() { PatientId = patient.Id, ProfessionalId = second.Id }, new() { PatientId = other.Id, ProfessionalId = professional.Id },
            new() { PatientId = other.Id, ProfessionalId = alien.Id }, new() { PatientId = patient.Id, ProfessionalId = inactive.Id });
        await db.SaveChangesAsync();
        var store = new NutritionCycleClosingNoticeStore(db, new PatientScopeStore(db));
        // El 26/nov UTC aún es 25/nov local: faltan seis días; no anticipar el aviso.
        Assert.Equal(0, await store.EmitAsync(new(2026, 11, 26, 4, 59, 0, TimeSpan.Zero), default));
        var boundary = new DateTimeOffset(2026, 11, 26, 5, 0, 0, TimeSpan.Zero);
        async Task<int> Emit()
        { await using var context = Context(); return await new NutritionCycleClosingNoticeStore(context, new PatientScopeStore(context)).EmitAsync(boundary, default); }
        Assert.Equal(4, (await Task.WhenAll(Emit(), Emit())).Sum());
        Assert.Equal(0, await store.EmitAsync(boundary.AddHours(1), default));
        Assert.Equal(4, await db.NotificationDedupeKeys.CountAsync()); Assert.Empty(await db.AppNotifications.ToArrayAsync());
        Assert.False(await db.NotificationDedupeKeys.AnyAsync(n => n.UserId == inactiveUser.UserId));
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(patient.UserId); actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(professional.Id);
        actor.HasPermissionAsync("Nutrition.View", Arg.Any<CancellationToken>()).Returns(true);
        var scope = new PatientScopeGuard(actor, new PatientScopeStore(db));
        var handler = new CycleClosingNoticesHandler(scope, actor, store, new NutritionCycleReadStore(db));
        Assert.Equal(2, (await handler.Handle(new GetCycleClosingNotices(), default)).Total);
        var revoked = await db.PatientProfessionalAssignments.SingleAsync(a => a.PatientId == other.Id && a.ProfessionalId == professional.Id);
        revoked.Status = "Inactive"; await db.SaveChangesAsync();
        var result = await handler.Handle(new GetCycleClosingNotices(), default);
        Assert.Equal(1, result.Total); var notice = Assert.Single(result.Items);
        Assert.Equal(plan.CycleId, notice.CycleId); Assert.Equal(patient.Id, notice.PatientId); Assert.Equal(new DateOnly(2026, 12, 1), notice.EndDate);
        Assert.Equal(boundary, notice.NotifiedAt); Assert.NotEmpty(notice.TitleEs); Assert.NotEmpty(notice.TitleEn);
        Assert.DoesNotContain(result.Items, n => n.PatientId == other.Id);
        Assert.Empty((await handler.Handle(new GetCycleClosingNotices(Page: 3), default)).Items);
        actor.IsAdmin.Returns(true); Assert.Equal(2, (await handler.Handle(new GetCycleClosingNotices(), default)).Total);
        actor.IsAdmin.Returns(false); actor.UserId.Returns(secondUser.UserId); actor.ProfessionalIdAsync(Arg.Any<CancellationToken>()).Returns(second.Id);
        Assert.Single((await handler.Handle(new GetCycleClosingNotices(), default)).Items);
        await db.NutritionCycles.Where(c => c.Id == plan.CycleId).ExecuteUpdateAsync(s => s.SetProperty(c => c.State, "revisado"));
        Assert.Equal(0, (await handler.Handle(new GetCycleClosingNotices(), default)).Total);
        Assert.Equal(0, await store.EmitAsync(boundary, default)); // No recrear el aviso del ciclo resuelto ni del destinatario revocado.
        actor.HasPermissionAsync("Nutrition.View", Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new GetCycleClosingNotices(), default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.EmitAsync(boundary, cancelled.Token));
        Assert.Equal(4, await db.NotificationDedupeKeys.CountAsync());
    }
}
