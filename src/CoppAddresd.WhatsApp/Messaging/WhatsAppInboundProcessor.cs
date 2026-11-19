using CoppAddresd.Application.Common;
using CoppAddresd.Application.Features.Chat;
using CoppAddresd.Application.Features.Patients;
using CoppAddresd.Application.WhatsApp.Messaging;
using CoppAddresd.Domain.Entities.WhatsApp;
using CoppAddresd.WhatsApp.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.WhatsApp.Messaging;

public sealed class WhatsAppInboundProcessor(
    WhatsAppDbContext db,
    WhatsAppPatientResolver resolver,
    ISender mediator,
    IWhatsAppMessageSender sender,
    TimeProvider clock,
    ILogger<WhatsAppInboundProcessor> logger,
    CoppAddresd.Application.WhatsApp.IWhatsAppReminderHooks? reminders = null,
    CoppAddresd.WhatsApp.Binding.WhatsAppBindingFlow? binding = null
)
{
    private const int MaxAttempts = 5;

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var lease = now.AddMinutes(5);
        // Claim atómico con transacción implícita corta. Ningún lock de fila vive durante el HTTP.
        // Un evento anterior pendiente bloquea solo su número: mantiene orden y memoria entre réplicas.
        var claimed = await db
            .RawEvents.FromSqlInterpolated(
                $"""
                UPDATE whatsapp.raw_events
                SET lease_until = CASE WHEN attempts < {MaxAttempts} THEN {lease} END,
                    attempts = CASE WHEN attempts < {MaxAttempts} THEN attempts + 1 ELSE attempts END,
                    processed_at = CASE WHEN attempts >= {MaxAttempts} THEN {now} END,
                    error_code = CASE WHEN attempts >= {MaxAttempts} THEN 'attempts_exhausted' ELSE error_code END
                WHERE id = (
                    SELECT e.id FROM whatsapp.raw_events e
                    WHERE e.processed_at IS NULL AND e.next_attempt_at <= {now}
                      AND (e.lease_until IS NULL OR e.lease_until <= {now})
                      AND NOT EXISTS (SELECT 1 FROM whatsapp.raw_events p
                        WHERE p.wa_id = e.wa_id AND p.processed_at IS NULL
                          AND (p.received_at, p.id) < (e.received_at, e.id))
                    ORDER BY e.received_at, e.id LIMIT 1 FOR UPDATE OF e SKIP LOCKED
                ) RETURNING *
                """
            )
            .ToListAsync(ct);
        if (claimed.Count == 0)
            return false;
        var input = claimed[0];
        // También cierra un quinto intento interrumpido cuyo lease ya venció.
        if (input.ProcessedAt is not null)
        {
            logger.LogWarning("Evento WhatsApp {EventId} cerrado: intentos agotados", input.Id);
            return true;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        // Indicador de escritura (tres puntos): se marca el mensaje como leído
        // y se refresca cada 20 s mientras se prepara la respuesta. Meta lo
        // descarta a los 25 s o al responder; es cosmético y nunca rompe el flujo.
        var typingCts =
            input.Reply is null && !string.IsNullOrEmpty(input.Wamid)
                ? StartTypingIndicator(input.Wamid, deadline.Token)
                : null;
        string? failureCode = null;
        var retry = true;
        try
        {
            if (input.Reply is null)
            {
                // Reserva tiempo para guardar y enviar el respaldo si la preparación expira.
                using var preparation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                preparation.CancelAfter(TimeSpan.FromSeconds(100));
                await PrepareReplyAsync(input, preparation.Token, deadline.Token);
            }
            if (input.ReplyFragments is null)
            {
                // Los eventos ya enviados en parte conservan los límites de la versión anterior.
                if (input.SentFragments > 0)
                    input.ReplyFragments = MessagePolicy.ResumeLegacyReply(input.Reply!);
                else
                {
                    var text = MessagePolicy.NormalizeForWhatsApp(input.Reply ?? "");
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        SetSafeReply(input, TurnLanguage.Detect(input.Text), "reply_empty");
                        text = input.Reply!;
                    }
                    input.ReplyFragments = MessagePolicy.Split(text);
                }
            }
            // Se fija la secuencia antes del primer envío para que su cursor sobreviva cualquier reintento.
            await db.SaveChangesAsync(deadline.Token);
            var fragments = input.ReplyFragments;
            for (var i = input.SentFragments; i < fragments.Length; i++)
            {
                var result = await sender.SendTextAsync(
                    input.WaId,
                    fragments[i],
                    input.MessageAt,
                    deadline.Token
                );
                if (!result.Succeeded)
                {
                    failureCode = result.ErrorCode ?? "send_failed";
                    retry = result.Failure == WhatsAppFailureKind.Transient;
                    await RecordFailedFragmentAsync(input, i, failureCode, deadline.Token);
                    break;
                }
                await RecordAcceptedFragmentAsync(input, i, result.Wamid!, deadline.Token);
            }
            if (failureCode is null)
            {
                input.ProcessedAt = clock.GetUtcNow();
                input.LeaseUntil = null;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Procesamiento WhatsApp fallido para {Phone}: {Cause}",
                MessagePolicy.MaskPhone(input.WaId),
                ex.GetType().Name
            );
            failureCode = IsTimeout(ex) ? "processing_timeout" : "processing_failed";
        }
        finally
        {
            // Corta el refresco del indicador en TODAS las salidas del turno.
            typingCts?.Cancel();
            typingCts?.Dispose();
        }
        if (failureCode is not null)
            await FailAsync(input, lease, failureCode, retry, ct);
        return true;
    }

    private async Task PrepareReplyAsync(
        WhatsAppRawEvent input,
        CancellationToken ct,
        CancellationToken deliveryToken
    )
    {
        var previousLanguage = "es";
        var unlinked = false;
        try
        {
            var link = await resolver.ResolveAsync(input.WaId, input.MessageAt, ct);
            if (link is null)
            {
                unlinked = true;
                if (binding is not null)
                    await binding.HandleAsync(input, ct);
                else
                {
                    input.Reply = Binding.WhatsAppBindingFlow.GuideInApp(TurnLanguage.Detect(input.Text));
                    Binding.WhatsAppBindingFlow.Scrub(input);
                }
                return;
            }

            previousLanguage = link.Language;
            if (reminders is not null)
                await reminders.CancelForPatientAsync(link.PatientId, ct);
            var conversation = await db.Conversations.SingleAsync(x => x.PatientId == link.PatientId, ct);
            var language = TurnLanguage.Detect(input.Text, conversation.Language);
            await PrepareLinkedReplyAsync(input, link, conversation, language, ct);
        }
        catch (OperationCanceledException) when (deliveryToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No persistir cambios parciales de vinculación o conversación junto al respaldo.
            db.ChangeTracker.Clear();
            await db.Entry(input).ReloadAsync(deliveryToken);
            var language = TurnLanguage.Detect(input.Text, previousLanguage);
            SetSafeReply(input, language, IsTimeout(ex) ? "preparation_timeout" : "preparation_failed", ex);
            if (unlinked)
                Binding.WhatsAppBindingFlow.Scrub(input);
        }
    }

    private async Task PrepareLinkedReplyAsync(
        WhatsAppRawEvent input,
        PatientLink link,
        Conversation conversation,
        string language,
        CancellationToken ct
    )
    {
        conversation.Language = link.Language = language;
        link.UpdatedAt = clock.GetUtcNow();
        var optOut = TurnLanguage.OptOutCommand(input.Text);
        if (optOut is not null)
        {
            link.OptOut = optOut.Value;
            input.Reply = (optOut.Value, language) switch
            {
                (true, "en") => "Reminders paused. Send START to resume.",
                (false, "en") => "Reminders resumed. Send STOP to pause.",
                (true, _) => "Recordatorios pausados. Envía INICIAR para reactivarlos.",
                _ => "Recordatorios reactivados. Envía SALIR para pausarlos.",
            };
        }
        else if (string.IsNullOrWhiteSpace(input.Text))
            input.Reply = language == "en"
                ? "Please send a text message to chat with your assistant."
                : "Envía un mensaje de texto para conversar con tu asistente.";
        else
        {
            if (input.AgentStarted)
            {
                SetSafeReply(input, language, "agent_result_unknown");
                return;
            }

            if (link.UserId is not { } userId || userId == Guid.Empty
                || !await mediator.Send(new VerifyPatientChannelLinkQuery(link.PatientId, userId), ct))
            {
                input.Reply = Binding.WhatsAppBindingFlow.GuideInApp(language);
                input.ErrorCode = "agent_identity_unavailable";
                return;
            }

            conversation.ThreadId = PatientChatThread.ForUser(userId);
            // Una ejecución cuyo resultado se perdió puede haber llamado tools de escritura.
            input.AgentStarted = true;
            await db.SaveChangesAsync(ct);
            await PrepareAgentReplyAsync(input, conversation, link, language, ct);
        }
    }

    internal async Task PrepareAgentReplyAsync(
        WhatsAppRawEvent input,
        Conversation conversation,
        PatientLink link,
        string language,
        CancellationToken ct
    )
    {
        using var agentDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        agentDeadline.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var result = await mediator.Send(
                new ChatCommand(
                    input.Text,
                    Agent: "whatsapp",
                    ThreadId: conversation.ThreadId,
                    UserId: link.UserId?.ToString(),
                    Language: language,
                    IncludeControlContext: false,
                    PatientId: link.PatientId.ToString(),
                    SupportsVita: true,
                    Channel: "whatsapp",
                    ChannelMessageId: input.Wamid
                ),
                agentDeadline.Token
            );
            if (!string.Equals(result.ThreadId, conversation.ThreadId, StringComparison.Ordinal))
            {
                SetSafeReply(input, language, "agent_thread_mismatch");
                return;
            }
            if (string.IsNullOrWhiteSpace(MessagePolicy.NormalizeForWhatsApp(result.Reply)))
            {
                SetSafeReply(input, language, "agent_empty_reply");
                return;
            }

            input.Reply = result.Reply;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var code = ex switch
            {
                _ when IsTimeout(ex) => "agent_timeout",
                AiServiceException => "agent_AiServiceException",
                _ => "agent_unexpected_error",
            };
            SetSafeReply(input, language, code, ex);
        }
    }

    /// <summary>
    /// Registra el fragmento aceptado por Meta y avanza el cursor en una sola sentencia, de modo
    /// que el ledger y <c>sent_fragments</c> nunca divergen. Un wamid repetido no rompe la entrega
    /// (ON CONFLICT DO NOTHING). Tras confirmar, concilia recibos que llegaron antes del ACK.
    /// </summary>
    private async Task RecordAcceptedFragmentAsync(
        WhatsAppRawEvent input,
        int index,
        string wamid,
        CancellationToken ct
    )
    {
        var acceptedAt = clock.GetUtcNow();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH ledger AS (
                INSERT INTO whatsapp.outbound_messages (id, wamid, wa_id, raw_event_id, fragment_index, accepted_at, status, status_at)
                VALUES ({Guid.NewGuid()}, {wamid}, {input.WaId}, {input.Id}, {index}, {acceptedAt}, 'sent', {acceptedAt})
                ON CONFLICT (wamid) DO NOTHING
            )
            UPDATE whatsapp.raw_events SET sent_fragments = {index + 1} WHERE id = {input.Id}
            """,
            ct
        );
        // El cursor ya está persistido: se sincroniza el tracker sin marcarlo como modificado.
        var cursor = db.Entry(input).Property(x => x.SentFragments);
        cursor.CurrentValue = index + 1;
        cursor.OriginalValue = index + 1;
        try
        {
            await MessageReceiptLedger.ReconcileOutboundAsync(db, wamid, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // El fragmento ya se entregó y su cursor está persistido: un fallo de conciliación no
            // debe marcar el evento como fallido. El recibo sigue en message_receipts.
            logger.LogWarning(
                "Conciliación de recibo del fragmento {Wamid} fallida: {Cause}",
                MessageReceiptLedger.MaskWamid(wamid),
                ex.GetType().Name
            );
        }
    }

    /// <summary>
    /// Deja constancia del intento rechazado (wamid nulo, <c>failed</c>). Es diagnóstico: si su
    /// INSERT falla se registra y no altera la política de reintento del evento.
    /// </summary>
    private async Task RecordFailedFragmentAsync(
        WhatsAppRawEvent input,
        int index,
        string errorCode,
        CancellationToken ct
    )
    {
        try
        {
            var error = MessageReceiptLedger.Truncate(errorCode);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO whatsapp.outbound_messages (id, wamid, wa_id, raw_event_id, fragment_index, accepted_at, status, status_at, error_code)
                VALUES ({Guid.NewGuid()}, NULL, {input.WaId}, {input.Id}, {index}, NULL, 'failed', {clock.GetUtcNow()}, {error})
                """,
                ct
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "No se pudo registrar el fragmento fallido del evento {EventId}: {Cause}",
                input.Id,
                ex.GetType().Name
            );
        }
    }

    private async Task FailAsync(
        WhatsAppRawEvent input,
        DateTimeOffset lease,
        string code,
        bool retry,
        CancellationToken ct
    )
    {
        var now = clock.GetUtcNow();
        var willRetry = retry && input.Attempts < MaxAttempts;
        var nextAttemptAt = willRetry
            ? now.AddSeconds(5 * Math.Pow(2, input.Attempts - 1))
            : input.NextAttemptAt;
        DateTimeOffset? processedAt = willRetry ? null : now;
        // Aísla el estado de la cola de cualquier cambio pendiente tras un error de persistencia.
        await db.RawEvents.Where(x => x.Id == input.Id && x.ProcessedAt == null && x.LeaseUntil == lease)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.ErrorCode, code)
                .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(x => x.ProcessedAt, processedAt)
                .SetProperty(x => x.NextAttemptAt, nextAttemptAt), ct);
        logger.LogWarning("Evento WhatsApp {EventId}: {Code}, intento {Attempt}, reintento {Retry}",
            input.Id, code, input.Attempts, willRetry);
    }

    private void SetSafeReply(WhatsAppRawEvent input, string language, string code, Exception? cause = null)
    {
        input.Reply = SafeFailure(language);
        input.ErrorCode = code;
        logger.LogWarning("Respuesta segura de WhatsApp para evento {EventId}, {Phone}: {Code}, {Cause}",
            input.Id, MessagePolicy.MaskPhone(input.WaId), code, cause?.GetType().Name);
    }

    private static bool IsTimeout(Exception exception) => exception is OperationCanceledException
        or TimeoutException or AiServiceException { StatusCode: 408 or 504 };

    private static string SafeFailure(string language) =>
        language == "en"
            ? "Your assistant is temporarily unavailable. Please try again in a few minutes."
            : "Tu asistente no está disponible en este momento. Inténtalo de nuevo en unos minutos.";

    /// <summary>
    /// Bucle del indicador de escritura: primera marca inmediata y refresco
    /// cada 20 s hasta que el proceso se complete o se cancele. Meta descarta
    /// el indicador a los 25 s, así que el refresco mantiene la animación
    /// durante todo el turno del agente.
    /// </summary>
    private CancellationTokenSource StartTypingIndicator(string wamid, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(
            async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await sender.ShowTypingAsync(wamid, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Indicador de escritura fallido (cosmético)");
                    }
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(20), cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            },
            CancellationToken.None
        );
        return cts;
    }
}
