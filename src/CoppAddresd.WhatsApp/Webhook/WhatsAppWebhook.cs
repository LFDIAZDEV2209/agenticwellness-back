using CoppAddresd.Application.Interfaces;
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
                    if (sosStatuses is not null && value.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Array)
                        foreach (var status in statuses.EnumerateArray()) {
                            var correlation=String(status,"biz_opaque_callback_data");
                            var statusId=String(status,"id"); var recipient=String(status,"recipient_id");
                            var state=String(status,"status");
                            if(correlation is null || statusId is null || recipient is null || state is null
                                || !long.TryParse(String(status,"timestamp"),out var seconds) || seconds<0 || seconds>253402300799) continue;
                            string? errorCode=null;
                            if(status.TryGetProperty("errors",out var errors) && errors.ValueKind==JsonValueKind.Array && errors.GetArrayLength()>0
                                && errors[0].ValueKind==JsonValueKind.Object && errors[0].TryGetProperty("code",out var code)
                                && code.ValueKind==JsonValueKind.Number && code.TryGetInt32(out var n)) errorCode="meta:"+n;
                            await sosStatuses.ReceiveAsync(correlation,statusId,recipient,state,DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime,errorCode,ct);
                        }
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

    private static string? String(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
