using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Application.Features.Media.PodcastMetadata;

/// <summary>Texto editorial seleccionado por el operador; sin audio, URLs, pacientes ni estado de publicación.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PodcastMetadataInput(string Title, string Language = "es", string Context = "");
public sealed record PodcastMetadataDraft(string Description, string Category, IReadOnlyList<string> Tags);
public sealed record PodcastMetadataResponse(string Status, bool IsEstimate, PodcastMetadataDraft Draft, string? Model, DateTimeOffset GeneratedAt);
/// <summary>Estimación editable y efímera; unavailable conserva el formulario manual del consumidor.</summary>
public sealed record PodcastMetadataResult(string Status, bool IsEstimate = true, PodcastMetadataDraft? Draft = null,
    string? Model = null, DateTimeOffset? GeneratedAt = null);
public sealed record SuggestPodcastMetadataCommand(PodcastMetadataInput Input) : IRequest<PodcastMetadataResult>;
public interface IPodcastMetadataClient
{
    Task<PodcastMetadataResponse> SuggestAsync(PodcastMetadataInput input, CancellationToken ct);
}
public sealed class PodcastMetadataUnavailableException : Exception
{
    public PodcastMetadataUnavailableException() : base("Sugerencia de metadatos no disponible.") { }
}

/// <summary>Límites del contrato AI a977e33 y del editor de medios existente.</summary>
public static class PodcastMetadataContract
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static bool PlainText(string? text) => text is not null && !Regex.IsMatch(text, @"https?://|<[^>]+>", RegexOptions.IgnoreCase);
    public static bool ValidInput(PodcastMetadataInput? input) => input is not null && !string.IsNullOrWhiteSpace(input.Title)
        && input.Title.Length <= 200 && input.Language is "es" or "en" && input.Context is { Length: <= 16000 }
        && PlainText(input.Title) && PlainText(input.Context);
    public static bool ValidResponse(PodcastMetadataResponse r) => r.Status == "ok" && r.IsEstimate
        && r.GeneratedAt != default && r.GeneratedAt.Offset == TimeSpan.Zero
        && (r.Model is null || !string.IsNullOrWhiteSpace(r.Model) && r.Model.Length <= 200)
        && r.Draft is { } d && !string.IsNullOrWhiteSpace(d.Description) && d.Description.Length <= 2000 && PlainText(d.Description)
        && Enum.GetNames<MediaCategory>().Contains(d.Category, StringComparer.Ordinal)
        && d.Tags is not null && ExternalMediaRules.ValidTags(d.Tags) && d.Tags.All(PlainText);
}

public sealed class SuggestPodcastMetadataCommandValidator : AbstractValidator<SuggestPodcastMetadataCommand>
{
    public SuggestPodcastMetadataCommandValidator()
    {
        RuleFor(r => r.Input).Must(PodcastMetadataContract.ValidInput)
            .WithMessage("Título de 1 a 200 caracteres, contexto de hasta 16000, idioma es/en y texto sin HTML ni URLs.");
    }
}

/// <summary>Asistencia a pedido: no conoce repositorios de medios, storage ni publicación.</summary>
public sealed class SuggestPodcastMetadataCommandHandler(IPatientScopeActor actor, IPodcastMetadataClient client,
    ILogger<SuggestPodcastMetadataCommandHandler> logger) : IRequestHandler<SuggestPodcastMetadataCommand, PodcastMetadataResult>
{
    private async Task EnsurePermissions(CancellationToken ct)
    {
        if (actor.UserId is not { } user || user == Guid.Empty) throw new ForbiddenException("Falta identidad del operador.");
        foreach (var permission in new[] { "Media.Create", "Program.AiManage" })
            if (!await actor.HasPermissionAsync(permission, ct)) throw new ForbiddenException("Falta permiso para sugerir metadatos.");
    }
    public async Task<PodcastMetadataResult> Handle(SuggestPodcastMetadataCommand request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsurePermissions(ct);
        var watch = Stopwatch.StartNew();
        try
        {
            var response = await client.SuggestAsync(request.Input with { Title = request.Input.Title.Trim(), Context = request.Input.Context.Trim() }, ct);
            ct.ThrowIfCancellationRequested();
            await EnsurePermissions(ct);
            logger.LogInformation("Media.PodcastMetadataSuggested ElapsedMs={ElapsedMs}", watch.ElapsedMilliseconds);
            return new("ok", true, response.Draft, response.Model, response.GeneratedAt);
        }
        catch (Exception ex) when (ex is PodcastMetadataUnavailableException or AiConsentException)
        {
            logger.LogWarning("Media.PodcastMetadataUnavailable Cause={Cause} ElapsedMs={ElapsedMs}", ex.GetType().Name, watch.ElapsedMilliseconds);
            return new("unavailable");
        }
    }
}
