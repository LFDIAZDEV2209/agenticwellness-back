using System.Security.Claims;
using CoppAddresd.Application.Features.LegalDocuments;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CoppAddresd.Application.Features.Agents.Vita;

namespace CoppAddresd.Infrastructure.Services;

/// <summary>Server-only metadata: never serialized or sent to an AI provider.</summary>
public sealed record AiConsentSubject(string Scope, Guid? PatientId = null, Guid? UserId = null,
    bool RequireRecipientConsent = false, bool RequireVoiceConsent = false)
{
    public static readonly HttpRequestOptionsKey<AiConsentSubject> Key = new("CoppAddresd.AiConsentSubject");
}

/// <summary>
/// Defense at the outgoing HTTP boundary, including background jobs. Factory handlers own a different
/// lifetime than requests, so resolve a fresh scoped DbContext for every check; never retain one here.
/// </summary>
public sealed class AiConsentHttpHandler(IServiceScopeFactory scopes, IHttpContextAccessor http,
    IOptionsMonitor<LegalConsentSettings>? policy = null) : DelegatingHandler
{
    // Solo el cliente interno de historial puede marcar esta retirada; no viene de cabeceras del usuario.
    internal static readonly HttpRequestOptionsKey<bool> ThreadHistoryDeletion = new("CoppAddresd.ThreadHistoryDeletion");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Borrar sigue permitido tras retirar el consentimiento de lectura/procesamiento.
        if (request.Method == HttpMethod.Delete
            && request.Options.TryGetValue(ThreadHistoryDeletion, out var clearHistory) && clearHistory)
            return await base.SendAsync(request, ct);

        request.Options.TryGetValue(AiConsentSubject.Key, out var subject);
        if (subject?.RequireVoiceConsent == true)
        {
            if (subject.UserId is not { } syncUser) throw new AiConsentException("CONSENT_SUBJECT_REQUIRED", "voice", 503);
            // Este GET sí procesa voz: no hereda el bypass de lecturas internas ni de operadores ERP.
            using var syncScope = scopes.CreateScope();
            await syncScope.ServiceProvider.GetRequiredService<VitaPatientAccess>().RequireChatAndVoiceAsync(syncUser, ct);
            return await base.SendAsync(request, ct);
        }
        if (policy?.CurrentValue.EnforcementEnabled == false)
            return await base.SendAsync(request, ct);

        // These internal reads/configuration operations do not invoke an external AI processor.
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (request.Method == HttpMethod.Get || path.EndsWith("/proactive-message", StringComparison.Ordinal))
            return await base.SendAsync(request, ct);

        var user = http.HttpContext?.User;
        // Deliberately preserve authenticated ERP/operator workflows; not a background bypass.
        if (subject?.RequireRecipientConsent != true
            && user?.Identity?.IsAuthenticated == true && user.HasClaim("aud", "erp") && !user.HasClaim("aud", "app"))
            return await base.SendAsync(request, ct);

        using var serviceScope = scopes.CreateScope();
        var consents = serviceScope.ServiceProvider.GetRequiredService<ILegalConsentService>();
        if (subject is not null)
        {
            if (subject.PatientId is { } patientId)
                await consents.EnsurePatientAsync(patientId, subject.Scope, ct);
            else if (subject.UserId is { } subjectUserId)
                await consents.EnsureUserAsync(subjectUserId, subject.Scope, ct);
            else if (user?.Identity?.IsAuthenticated == true
                     && Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var currentUserId))
                await consents.EnsureUserAsync(currentUserId, subject.Scope, ct);
            else throw new AiConsentException("CONSENT_SUBJECT_REQUIRED", subject.Scope, 503);
        }
        else if (user?.Identity?.IsAuthenticated == true
                 && Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            await consents.EnsureUserAsync(userId, LegalConsentScopes.PatientAi, ct);
        else
            throw new AiConsentException("CONSENT_SUBJECT_REQUIRED", LegalConsentScopes.PatientAi, 503);
        return await base.SendAsync(request, ct);
    }
}
