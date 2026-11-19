namespace CoppAddresd.Domain.Entities.WhatsApp;

/// <summary>
/// Estados de entrega que Meta notifica en <c>statuses</c>. Se guardan como texto en minúsculas,
/// igual que en el payload, para que una consulta SQL directa sea legible.
/// </summary>
public enum MessageStatus
{
    sent,
    delivered,
    read,
    failed,
}

/// <summary>
/// Máquina de estados monótona de los recibos: <c>sent &lt; delivered &lt; read</c> y
/// <c>failed</c> siempre gana. Un estado nunca retrocede, así el orden de llegada de los
/// webhooks (que Meta no garantiza) no altera el resultado final.
/// </summary>
public static class MessageStatusOrder
{
    public static int Rank(MessageStatus status) =>
        status switch
        {
            MessageStatus.sent => 1,
            MessageStatus.delivered => 2,
            MessageStatus.read => 3,
            MessageStatus.failed => 4,
            _ => 0,
        };

    /// <summary>Indica si <paramref name="next"/> debe reemplazar a <paramref name="current"/>.</summary>
    public static bool Advances(MessageStatus? current, MessageStatus next) =>
        current is null || Rank(next) > Rank(current.Value);

    /// <summary>Interpreta el estado de Meta; devuelve null si no es uno de los cuatro conocidos.</summary>
    public static MessageStatus? Parse(string? value) =>
        value switch
        {
            "sent" => MessageStatus.sent,
            "delivered" => MessageStatus.delivered,
            "read" => MessageStatus.read,
            "failed" => MessageStatus.failed,
            _ => null,
        };
}

/// <summary>
/// Recibo append-only de Meta para cualquier mensaje saliente (chat, recordatorios, SOS).
/// Se guarda siempre, aunque el wamid aún no exista en ninguna tabla local: así un recibo que
/// llega antes del ACK HTTP del envío puede conciliarse después.
/// </summary>
public sealed class MessageReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Wamid { get; set; } = "";
    public MessageStatus Status { get; set; }
    public string? RecipientId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Ledger de fragmentos de respuesta del chat enviados a Meta. Un envío aceptado tiene wamid y
/// estado inicial <c>sent</c>; un envío rechazado queda con wamid nulo, estado <c>failed</c> y
/// el código de error del proveedor.
/// </summary>
public sealed class OutboundMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Wamid { get; set; }
    public string WaId { get; set; } = "";
    public Guid RawEventId { get; set; }
    public int FragmentIndex { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public MessageStatus Status { get; set; } = MessageStatus.sent;
    public DateTimeOffset? StatusAt { get; set; }
    public string? ErrorCode { get; set; }
}
