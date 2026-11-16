using CoppAddresd.Application.Features.ProgramProgress.Queries.GetRoutineUsage;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Application.Services;
using CoppAddresd.Application.Services.ProgramProgress;
using CoppAddresd.Domain.Exceptions;
using NSubstitute;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class RoutineUsageQueryTests
{
    [Fact]
    public async Task AdminSinPermiso_NoLeeCatalogoNiPacientes()
    {
        var actor = Substitute.For<IPatientScopeActor>(); actor.UserId.Returns(Guid.NewGuid()); actor.IsAdmin.Returns(true);
        actor.HasPermissionAsync("Wellness.View", Arg.Any<CancellationToken>()).Returns(false);
        var store = Substitute.For<IProgramRoutineUsageStore>();
        var handler = new GetRoutineUsageQueryHandler(new PatientScopeGuard(actor, Substitute.For<IPatientScopeStore>()), store,
            Substitute.For<IEffectiveDayContentResolver>());
        await Assert.ThrowsAsync<ForbiddenException>(() => handler.Handle(new(Guid.NewGuid()), default));
        await store.DidNotReceiveWithAnyArgs().LoadAsync(default, default!, default);
    }

    [Fact]
    public async Task Cancelado_NoLeeNiDevuelveUnCeroInventado()
    {
        var store = Substitute.For<IProgramRoutineUsageStore>();
        var handler = new GetRoutineUsageQueryHandler(new PatientScopeGuard(Substitute.For<IPatientScopeActor>(), Substitute.For<IPatientScopeStore>()),
            store, Substitute.For<IEffectiveDayContentResolver>());
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.Handle(new(Guid.NewGuid()), cancel.Token));
        await store.DidNotReceiveWithAnyArgs().LoadAsync(default, default!, default);
    }
}
