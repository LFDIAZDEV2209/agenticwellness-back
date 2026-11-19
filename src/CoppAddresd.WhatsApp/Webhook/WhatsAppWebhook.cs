using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Entities.WhatsApp;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoppAddresd.WhatsApp.Messaging;
using CoppAddresd.WhatsApp.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.WhatsApp.Webhook;

public sealed class WhatsAppWebhook(
    WhatsAppDbContext db,
    WhatsAppOptions options,
    WhatsAppQueue queue,
    TimeProvider clock,
    ILogger<WhatsAppWebhook> logger,
    ISosWhatsAppStatusReceiver? sosStatuses = null
)
{
    public bool Verify(string? mode, string? token) =>
        mode == "subscribe"
        && token is not null
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            SHA256.HashData(Encoding.UTF8.GetBytes(options.WebhookVerifyToken))
        );

    public static bool ValidSignature(ReadOnlySpan<byte> body, string? header, string secret)
    {
        if (
            header is null
            || !header.StartsWith("sha256=", StringComparison.Ordinal)
            || header.Length != 71
        )
            return false;
        byte[] actual;
        try
        {
            actual = Convert.FromHexString(header.AsSpan(7));
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            actual,
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)
        );
    }

    public async Task<int> AcceptAsync(byte[] body, string? signature, CancellationToken ct)
    {
        try
        {
            return await PersistAsync(body, signature, ct);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or TimeoutException)
        {
            // No confirmar una entrega que PostgreSQL no pudo recibir. Un reenvío conserva el wamid.
            logger.LogWarning("Webhook WhatsApp no pudo persistir la entrega: {Cause}", ex.GetType().Name);
            return 429;
        }
    }

    private async Task<int> PersistAsync(byte[] body, string? signature, CancellationToken ct)
    {
        if (!ValidSignature(body, signature, options.AppSecret))
        {
            logger.LogWarning("Webhook WhatsApp rechazado: firma ausente o inválida");
            return 401;
        }
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return 400;
        }
        using (json)
        {
            if (
                String(json.RootElement, "object") != "whatsapp_business_account"
            )
                return 200;
            if (
                !json.RootElement.TryGetProperty("entry", out var entries)
                || entries.ValueKind != JsonValueKind.Array
            )
                return 200;
            var payload = Encoding.UTF8.GetString(body);
            var now = clock.GetUtcNow();
            foreach (var entry in entries.EnumerateArray())
            {
                if (
                    entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("changes", out var changes)
                    || changes.ValueKind != JsonValueKind.Array
                )
                    continue;
                foreach (var change in changes.EnumerateArray())
                {
                    if (
                        change.ValueKind != JsonValueKind.Object
                        || !change.TryGetProperty("value", out var value)
                        || value.ValueKind != JsonValueKind.Object
                        || !value.TryGetProperty("metadata", out var metadata)
                    )
                        continue;
                    var receiver = String(metadata, "phone_number_id");
                    // Un mensaje destinado a otro número de esta app (p.ej. el número de prueba
                    // reemplazado) se descarta; sin este log el caso queda sin rastro en prod
                    // y se percibe como "el bot no responde".
                    if (receiver != options.PhoneNumberId)
                    {
                        logger.LogInformation(
                            "Evento WhatsApp descartado: receptor {Receiver} no es el emisor configurado",
                            MessagePolicy.MaskPhone(receiver ?? "")
                        );
                        continue;
                    }
                    if (
                        value.TryGetProperty("statuses", out var statuses)
                        && statuses.ValueKind == JsonValueKind.Array
                    )
                        foreach (var status in statuses.EnumerateArray())
                            await AcceptStatusAsync(status, now, ct);
                    if(!value.TryGetProperty("messages",out var messages) || messages.ValueKind!=JsonValueKind.Array)continue;
                    foreach (var message in messages.EnumerateArray())
                    {
                        var wamid = String(message, "id");
                        var from = String(message, "from");
                        if (
                            string.IsNullOrWhiteSpace(wamid)
                            || wamid.Length > 512
                            || string.IsNullOrWhiteSpace(from)
                            || from.Length is < 8 or > 15
                            || !from.All(char.IsAsciiDigit)
                        )
                            continue;
                        if (
                            !long.TryParse(String(message, "timestamp"), out var unix)
                            || unix < 0
                            || unix > 253402300799
                        )
                            continue;
                        var messageAt = DateTimeOffset.FromUnixTimeSeconds(unix);
                        if (messageAt > now)
                            messageAt = now;
                        var text =
                            String(message, "type") == "text"
                            && message.TryGetProperty("text", out var textObject)
                                ? String(textObject, "body") ?? ""
                                : "";
                        if (String(message, "type") == "button" && message.TryGetProperty("button", out var button)
                            && String(button, "payload") is { } payloadText
                            && MedicationWhatsAppChannel.IsDoseReply(payloadText))
                            text = payloadText;
                        var id = Guid.NewGuid();
                        var waId = "+" + from;
                        // El evento y la interacción se guardan juntos. En conflicto solo avanza el contador.
                        await db.Database.ExecuteSqlInterpolatedAsync(
                            $"""
                            WITH accepted AS (
                                INSERT INTO whatsapp.raw_events
                                (id, wamid, payload, wa_id, text, received_at, message_at, next_attempt_at, attempts, duplicate_count, sent_fragments)
                                VALUES ({id}, {wamid}, CAST({payload} AS jsonb), {waId}, {text}, {now}, {messageAt}, {now}, 0, 0, 0)
                                ON CONFLICT (wamid) DO UPDATE SET duplicate_count = whatsapp.raw_events.duplicate_count + 1
                                RETURNING id, wa_id, message_at
                            ), interaction AS (
                                SELECT l.patient_id, a.message_at FROM accepted a
                                JOIN whatsapp.patient_links l ON l.wa_id = a.wa_id
                                WHERE a.id = {id}
                            ), updated_conversation AS (
                                UPDATE whatsapp.conversations c SET last_inbound_at = GREATEST(c.last_inbound_at, i.message_at)
                                FROM interaction i WHERE c.patient_id = i.patient_id
                            )
                            UPDATE whatsapp.reminders r SET state = 'Cancelled', cancellation_reason = 'patient_interaction', lease_until = NULL
                            FROM interaction i
                            WHERE r.patient_id = i.patient_id AND r.state = 'Pending' AND i.message_at >= r.scheduled_at
                            """,
                            ct
                        );
                        queue.Enqueue(id);
                        logger.LogInformation(
                            "Evento WhatsApp persistido para {Phone}",
                            MessagePolicy.MaskPhone(waId)
                        );
                    }
                }
            }
        }
        return 200;
    }

    /// <summary>
    /// Persiste el recibo localmente (siempre primero) y después lo reenvía a SOS con la misma
    /// semántica previa. Un wamid desconocido solo deja el recibo en <c>message_receipts</c>.
    /// </summary>
    private async Task AcceptStatusAsync(JsonElement status, DateTimeOffset now, CancellationToken ct)
    {
        var statusId = String(status, "id");
        var recipient = String(status, "recipient_id");
        var state = String(status, "status");
        if (
            statusId is null
            || state is null
            || !long.TryParse(String(status, "timestamp"), out var seconds)
            || seconds < 0
            || seconds > 253402300799
        )
            return;
        string? errorCode = null;
        if (
            status.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0
            && errors[0].ValueKind == JsonValueKind.Object
            && errors[0].TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var n)
        )
            errorCode = "meta:" + n;
        var occurredAt = DateTimeOffset.FromUnixTimeSeconds(seconds);

        var parsed = MessageStatusOrder.Parse(state);
        if (parsed is null)
            logger.LogInformation(
                "Recibo WhatsApp ignorado: estado {Status} no soportado para {Wamid}",
                state.Length > 20 ? state[..20] : state,
                MessageReceiptLedger.MaskWamid(statusId)
            );
        else if (statusId.Length > MessageReceiptLedger.MaxWamidLength)
            logger.LogWarning("Recibo WhatsApp ignorado: wamid excede {Max} caracteres", MessageReceiptLedger.MaxWamidLength);
        else
        {
            // recipient_id es el número del destinatario: solo se guarda si tiene forma de MSISDN.
            var recipientId =
                recipient is { Length: >= 8 and <= 20 } && recipient.All(char.IsAsciiDigit) ? recipient : null;
            var inserted = await MessageReceiptLedger.RecordAsync(
                db, statusId, parsed.Value, recipientId, occurredAt, errorCode, now, ct);
            logger.LogInformation(
                "Recibo WhatsApp {Status} para {Wamid}: {Result}",
                parsed.Value,
                MessageReceiptLedger.MaskWamid(statusId),
                inserted ? "persistido" : "duplicado"
            );
        }

        var correlation = String(status, "biz_opaque_callback_data");
        if (sosStatuses is null || correlation is null || recipient is null)
            return;
        try
        {
            await sosStatuses.ReceiveAsync(correlation, statusId, recipient, state, occurredAt.UtcDateTime, errorCode, ct);
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or TimeoutException)
        {
            // Mismo comportamiento previo: 429 para que Meta reenvíe. El recibo local ya quedó
            // persistido y es idempotente, así que el reenvío no lo duplica.
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(
                "Receptor SOS falló procesando el recibo {Wamid}: {Cause}",
                MessageReceiptLedger.MaskWamid(statusId),
                ex.GetType().Name
            );
        }
    }

    private static string? String(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
