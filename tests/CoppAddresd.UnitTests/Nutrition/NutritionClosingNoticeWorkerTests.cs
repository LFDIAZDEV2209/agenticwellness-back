using CoppAddresd.Application.Features.Nutrition.Queries;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CoppAddresd.UnitTests.Nutrition;

public sealed class NutritionClosingNoticeWorkerTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TickExistente_EmiteAviso_YFalloNoBloqueaGeneracion(bool failure)
    {
        var workflow = Substitute.For<INutritionDraftWorkflow>(); var notices = Substitute.For<INutritionCycleClosingNoticeStore>();
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow.ProcessNextAsync(Arg.Any<CancellationToken>()).Returns(_ => { processed.TrySetResult(); return false; });
        notices.EmitAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(_ => failure
            ? Task.FromException<int>(new InvalidOperationException("fallo sintético")) : Task.FromResult(1));
        using var services = new ServiceCollection().AddScoped(_ => workflow).AddScoped(_ => notices).BuildServiceProvider();
        using var worker = new NutritionDraftGenerationWorker(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<NutritionDraftGenerationWorker>.Instance);
        await worker.StartAsync(default); await processed.Task.WaitAsync(TimeSpan.FromSeconds(5)); await worker.StopAsync(default);
        await notices.Received(1).EmitAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await workflow.Received(1).ReconcileAsync(Arg.Any<CancellationToken>());
    }
}
