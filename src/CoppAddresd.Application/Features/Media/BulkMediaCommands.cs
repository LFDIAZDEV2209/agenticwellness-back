using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Application.Features.Media;

public sealed record BulkMediaFile(string Nombre, string? StorageKey, string? ContentType = null,
    long? FileSizeBytes = null, int? DurationSecs = null, string? Title = null, string Author = "",
    MediaCategory Category = MediaCategory.Biologia, string? ThumbnailKey = null);
public sealed record BulkMediaPublishFile(string Nombre, Guid MediaId);
public sealed record BulkMediaFileResult(string Nombre, string Estado, Guid? MediaId = null, string? Error = null);
public sealed record CreateBulkMediaDraftsCommand(IReadOnlyList<BulkMediaFile> Files, Guid? ActorId = null)
    : IRequest<IReadOnlyList<BulkMediaFileResult>>;
public sealed record PublishBulkMediaCommand(IReadOnlyList<BulkMediaPublishFile> Files)
    : IRequest<IReadOnlyList<BulkMediaFileResult>>;

public sealed class CreateBulkMediaDraftsValidator : AbstractValidator<CreateBulkMediaDraftsCommand>
{
    public CreateBulkMediaDraftsValidator() => RuleFor(x => x.Files).NotNull().Must(x => x is { Count: >= 1 and <= 100 })
        .WithMessage("El lote debe contener de 1 a 100 archivos.");
}
public sealed class PublishBulkMediaValidator : AbstractValidator<PublishBulkMediaCommand>
{
    public PublishBulkMediaValidator() => RuleFor(x => x.Files).NotNull().Must(x => x is { Count: >= 1 and <= 100 })
        .WithMessage("El lote debe contener de 1 a 100 archivos.");
}

/// <summary>Un scope por archivo aísla fallos de persistencia y conserva los resultados correctos.</summary>
public sealed class BulkMediaCommandsHandler(IServiceScopeFactory scopes, ILogger<BulkMediaCommandsHandler> logger)
    : IRequestHandler<CreateBulkMediaDraftsCommand, IReadOnlyList<BulkMediaFileResult>>,
      IRequestHandler<PublishBulkMediaCommand, IReadOnlyList<BulkMediaFileResult>>
{
    public async Task<IReadOnlyList<BulkMediaFileResult>> Handle(CreateBulkMediaDraftsCommand request, CancellationToken ct)
    {
        var result = new List<BulkMediaFileResult>();
        foreach (var file in request.Files)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(await ProcessAsync(file.Nombre, async services =>
            {
                var repository = services.GetRequiredService<IMediaItemRepository>();
                var existing = !string.IsNullOrWhiteSpace(file.StorageKey)
                    ? await repository.GetByStorageKeyAsync(file.StorageKey, ct) : null;
                // Reintentar un lote no reescribe los archivos ya correctos ni despublica medios.
                if (existing is not null) return Existing(file.Nombre, existing.Id, existing.Status);
                var title = string.IsNullOrWhiteSpace(file.Title)
                    ? Path.GetFileNameWithoutExtension(file.Nombre.Replace('\\', '/').Split('/')[^1]) : file.Title.Trim();
                var created = await services.GetRequiredService<ISender>().Send(new CreateMediaItemCommand(
                    title, null, file.Author, MediaType.Podcast, file.Category, file.StorageKey, file.ThumbnailKey,
                    file.ContentType, file.FileSizeBytes, file.DurationSecs, MediaStatus.Draft, 0, 0, 0, request.ActorId), ct);
                return new(file.Nombre, "borrador", created.Id);
            }, ct));
        }
        return result;
    }

    public async Task<IReadOnlyList<BulkMediaFileResult>> Handle(PublishBulkMediaCommand request, CancellationToken ct)
    {
        var result = new List<BulkMediaFileResult>();
        foreach (var file in request.Files)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(await ProcessAsync(file.Nombre, async services =>
            {
                var media = await services.GetRequiredService<IMediaItemRepository>().GetByIdAsync(file.MediaId, ct)
                    ?? throw new NotFoundException("Medio no encontrado.");
                if (media.Status == MediaStatus.Published) return new(file.Nombre, "publicado", media.Id);
                if (media.Status == MediaStatus.Archived) throw new UnprocessableEntityException("El medio está archivado.");
                await services.GetRequiredService<ISender>().Send(new PublishMediaItemCommand(file.MediaId), ct);
                return new(file.Nombre, "publicado", file.MediaId);
            }, ct));
        }
        return result;
    }

    private static BulkMediaFileResult Existing(string name, Guid id, MediaStatus status) => status switch
    {
        MediaStatus.Draft => new(name, "borrador", id),
        MediaStatus.Published => new(name, "publicado", id),
        _ => new(name, "error", id, "El archivo ya pertenece a un medio archivado.")
    };

    private async Task<BulkMediaFileResult> ProcessAsync(string name, Func<IServiceProvider, Task<BulkMediaFileResult>> action, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name)) throw new UnprocessableEntityException("El nombre del archivo es requerido.");
            await using var scope = scopes.CreateAsyncScope();
            return await action(scope.ServiceProvider);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ValidationException ex) { return new(name, "error", Error: string.Join(" ", ex.Errors.Select(e => e.ErrorMessage).Distinct())); }
        catch (UnprocessableEntityException ex) { return new(name, "error", Error: ex.Message); }
        catch (NotFoundException ex) { return new(name, "error", Error: ex.Message); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falló una fila del lote de medios; puede reintentarse");
            return new(name, "error", Error: "No se pudo procesar este archivo. Vuelve a intentarlo.");
        }
    }
}
