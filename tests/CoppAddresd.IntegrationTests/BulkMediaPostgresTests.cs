using CoppAddresd.Application.Common.Behaviors;
using CoppAddresd.Application.DTOs.Storage;
using CoppAddresd.Application.Features.Media;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Infrastructure.Repositories;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace CoppAddresd.IntegrationTests;

public sealed partial class NutritionPlanWorkflowTests
{
    [NutritionPostgresFact]
    public async Task LoteMedios_CincoArchivosUnoFalla_ReintentosSinRepetirCorrectos()
    {
        var storage = Substitute.For<IObjectStorageService>();
        var services = new ServiceCollection(); services.AddLogging();
        services.AddScoped(_ => Context()); services.AddScoped<IMediaItemRepository, MediaItemRepository>();
        services.AddSingleton(storage);
        services.AddMediatR(c => c.RegisterServicesFromAssembly(typeof(CreateMediaItemCommand).Assembly));
        services.AddValidatorsFromAssemblyContaining<CreateMediaItemCommandValidator>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope(); var mediator = scope.ServiceProvider.GetRequiredService<ISender>();
        var files = Enumerable.Range(1, 5).Select(n => new BulkMediaFile($"Episodio {n}.mp3", $"media/podcasts/lote-{n}.mp3",
            "audio/mpeg", 1024, n == 5 ? 0 : 60, Author: "Equipo")).ToArray();
        var draft = await mediator.Send(new CreateBulkMediaDraftsCommand(files));
        Assert.Equal(4, draft.Count(r => r.Estado == "borrador"));
        Assert.NotEmpty(Assert.Single(draft, r => r.Estado == "error").Error!);
        await using var db = Context(); Assert.Equal(4, await db.MediaItems.CountAsync());
        Assert.Equal("Episodio 1", (await db.MediaItems.AsNoTracking().SingleAsync(m => m.StorageKey == files[0].StorageKey)).Title);
        var retry = await mediator.Send(new CreateBulkMediaDraftsCommand([files[4] with { DurationSecs = 60 }]));
        Assert.Equal("borrador", Assert.Single(retry).Estado); Assert.Equal(5, await db.MediaItems.CountAsync());
        var allDrafts = await mediator.Send(new CreateBulkMediaDraftsCommand(files.Select(f => f with { DurationSecs = 60 }).ToArray()));
        Assert.Equal(5, await db.MediaItems.CountAsync());
        foreach (var file in files.Take(4))
            storage.HeadObjectAsync(file.StorageKey!, Arg.Any<CancellationToken>()).Returns(new ObjectMetadata(file.StorageKey!, 1024, null, "audio/mpeg", DateTimeOffset.UtcNow));
        var toPublish = allDrafts.Select(r => new BulkMediaPublishFile(r.Nombre, r.MediaId!.Value)).ToArray();
        var publication = await mediator.Send(new PublishBulkMediaCommand(toPublish));
        Assert.Equal(4, publication.Count(r => r.Estado == "publicado")); Assert.Single(publication, r => r.Estado == "error");
        storage.HeadObjectAsync(files[4].StorageKey!, Arg.Any<CancellationToken>()).Returns(new ObjectMetadata(files[4].StorageKey!, 1024, null, "audio/mpeg", DateTimeOffset.UtcNow));
        var publishedRetry = await mediator.Send(new PublishBulkMediaCommand([toPublish[4]]));
        Assert.Equal("publicado", Assert.Single(publishedRetry).Estado);
        var timestamps = await db.MediaItems.AsNoTracking().OrderBy(m => m.Id).Select(m => m.UpdatedAt).ToArrayAsync();
        Assert.All(await mediator.Send(new PublishBulkMediaCommand(toPublish)), r => Assert.Equal("publicado", r.Estado));
        Assert.All(await mediator.Send(new CreateBulkMediaDraftsCommand(files)), r => Assert.Equal("publicado", r.Estado));
        Assert.Equal(timestamps, await db.MediaItems.AsNoTracking().OrderBy(m => m.Id).Select(m => m.UpdatedAt).ToArrayAsync());
        foreach (var file in files.Take(4)) await storage.Received(1).HeadObjectAsync(file.StorageKey!, Arg.Any<CancellationToken>());
        await storage.Received(2).HeadObjectAsync(files[4].StorageKey!, Arg.Any<CancellationToken>());
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mediator.Send(new PublishBulkMediaCommand(toPublish), cancelled.Token));
    }
}
