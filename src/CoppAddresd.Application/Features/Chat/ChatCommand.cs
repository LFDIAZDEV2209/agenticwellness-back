using CoppAddresd.Application.DTOs.Ai;
using MediatR;

namespace CoppAddresd.Application.Features.Chat;

public record ChatCommand(
    string Message,
    string? Agent = null,
    string? ThreadId = null,
    string? AgentTypeId = null,
    string? UserId = null,
    string? Language = null,
    bool IncludeControlContext = true,
    string? PatientId = null,
    bool SupportsVita = false)
    : IRequest<ChatResult>;

/// <summary>
/// Resultado del chat síncrono expuesto a los clientes. <c>Suggestions</c> son
/// las sugerencias compatibles que el ai-service
/// adjunta a la respuesta; es null cuando no hay sugerencias.
/// </summary>
public record ChatResult(
    string Reply,
    string ThreadId,
    string? ExecutionId = null,
    string? Agent = null,
    IReadOnlyList<ChatSuggestion>? Suggestions = null,
    IReadOnlyList<CoppAddresd.Application.Features.Agents.Vita.VitaActionDto>? Actions = null,
    System.Text.Json.JsonElement? Sources = null,
    string? VersionId = null);
