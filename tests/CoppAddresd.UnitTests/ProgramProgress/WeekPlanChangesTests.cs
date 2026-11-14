using CoppAddresd.Application.Features.ProgramProgress.Queries.Erp;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using NSubstitute;

namespace CoppAddresd.UnitTests.ProgramProgress;

public sealed class WeekPlanChangesTests
{
    [Fact]
    public async Task Ajeno_NoConsultaNiCuentaLaBitacora()
    {
        var repository = Substitute.For<IProgramRepository>(); var guard = Substitute.For<IPatientScopeGuard>();
        guard.EnsureResourceAsync(PatientResource.Enrollment, Arg.Any<Guid>(), "Program.Profile360View", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Guid>(new NotFoundException("No encontrado")));
        await Assert.ThrowsAsync<NotFoundException>(() => new GetWeekPlanChangesHandler(repository, guard).Handle(new(Guid.NewGuid(), 2), default));
        await repository.DidNotReceive().GetWeekPlanChangesAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 101)]
    public void RechazaPaginacionInvalida(int page, int size)
        => Assert.False(new GetWeekPlanChangesValidator().Validate(new GetWeekPlanChangesQuery(Guid.NewGuid(), 2, page, size)).IsValid);
}
