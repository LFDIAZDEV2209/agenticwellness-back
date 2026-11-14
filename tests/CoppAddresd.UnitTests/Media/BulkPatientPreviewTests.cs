using CoppAddresd.Api.Context;
using CoppAddresd.Api.Controllers;
using CoppAddresd.Application.Features.ProgramProgress.Commands.BulkAssignProgramContent;
using CoppAddresd.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CoppAddresd.UnitTests.Media;

public sealed class BulkPatientPreviewTests
{
    [Fact]
    public async Task RutaPreview_FuerzaSimulacionAunqueElCuerpoPidaEscritura()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<BulkAssignPatientPodcastsCommand>(), Arg.Any<CancellationToken>()).Returns(new BulkAssignPatientPodcastsResult(1, 0, []));
        var controller = new ProgramController(mediator, NullLogger<ProgramController>.Instance, new ConfigurationBuilder().Build(),
            Substitute.For<IProgramActorContext>(), Substitute.For<IObjectStorageService>());
        var command = new BulkAssignPatientPodcastsCommand([Guid.NewGuid()], Guid.NewGuid(), 2, 3);
        await controller.PreviewBulkAssignPatientPodcasts(command, default);
        await mediator.Received(1).Send(Arg.Is<BulkAssignPatientPodcastsCommand>(c => c.Simulate && c.MediaId == command.MediaId
            && c.SemanaDesde == 2 && c.SemanaHasta == 3), Arg.Any<CancellationToken>());
        Assert.False(command.Simulate);
    }
}
