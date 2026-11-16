using CoppAddresd.Application.DTOs.Ai;
using CoppAddresd.Application.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Domain.Exceptions;

namespace CoppAddresd.Application.Features.Chat;

/// <summary>
/// Proxy de apertura de sesión de voz hacia el ai-service. Propaga el
/// <c>CancellationToken</c> de extremo a extremo y no filtra trazas internas:
/// los errores tipados (<c>AiServiceException</c>) los traduce el controller.
/// Auditoría estructurada SIN el signed URL (es la credencial temporal del
/// paciente) ni la API key.
/// </summary>
public sealed class VoiceSessionCommandHandler(
    IAiServiceClient aiService,
    ILogger<VoiceSessionCommandHandler> logger,
    VitaPatientAccess access
) : IRequestHandler<VoiceSessionCommand, VoiceSessionResponseDto>
{
    public async Task<VoiceSessionResponseDto> Handle(
        VoiceSessionCommand request,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParse(request.UserId, out var userId)) throw new ForbiddenException("VITA_PATIENT_UNAVAILABLE");
        var patient = await access.RequireAsync(userId, "voice", ct);

        var result = await aiService.CreateVoiceSessionAsync(
            new VoiceSessionInternalRequest(userId.ToString(), patient.Id.ToString(), request.ThreadId,
                request.Language, request.SupportsVitaVoice),
            ct
        );

        logger.LogInformation("Sesión de voz creada: modo={RuntimeMode}", result.RuntimeMode);

        return result;
    }
}
