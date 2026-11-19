using CoppAddresd.Domain.Entities.WhatsApp;
using CoppAddresd.WhatsApp.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.WhatsApp.Messaging;

/// <summary>
/// Persistencia de recibos de entrega de Meta y su conciliación con los salientes locales
/// (<c>outbound_messages</c> y <c>reminder_deliveries</c>).
/// <para>
/// Invariante: el estado solo avanza según <see cref="MessageStatusOrder"/> (<c>sent &lt;
/// delivered &lt; read</c>, <c>failed</c> gana). El SQL replica ese orden con <see cref="RankSql"/>;
/// los tests de integración verifican que ambos coincidan.
/// </para>
/// <para>
/// Carrera recibo/ACK: si Meta notifica antes de que el saliente se confirme en BD, el UPDATE del
/// webhook no encuentra fila; por eso quien registra el saliente llama a <c>Reconcile*Async</c>
/// DESPUÉS de confirmar su INSERT, y aplica el recibo más avanzado ya persistido. Queda una
/// ventana teórica de milisegundos (ambas sentencias concurrentes en READ COMMITTED sin verse);
/// el recibo sigue guardado en <c>message_receipts</c> y la siguiente notificación la corrige.
/// </para>
/// </summary>
public static class MessageReceiptLedger
{
    public const int MaxWamidLength = 512;
    public const int MaxErrorCodeLength = 80;

    /// <summary>Expresión SQL del rango de un estado; debe coincidir con <see cref="MessageStatusOrder.Rank"/>.</summary>
    private static string RankSql(string column) =>
        $"CASE {column} WHEN 'sent' THEN 1 WHEN 'delivered' THEN 2 WHEN 'read' THEN 3 WHEN 'failed' THEN 4 ELSE 0 END";

    // Solo se interpolan constantes (RankSql); todos los valores viajan como parámetros {n}.
    private static readonly string BestReceiptSql = $$"""
        SELECT r.status, r.occurred_at, r.error_code FROM whatsapp.message_receipts r
        WHERE r.wamid = {0} ORDER BY {{RankSql("r.status")}} DESC, r.occurred_at DESC LIMIT 1
        """;

    // Los CTE de escritura se ejecutan siempre en PostgreSQL. Los UPDATE no dependen de si el
    // recibo era duplicado: la condición de rango ya los hace idempotentes.
    private static readonly string RecordSql = $$"""
        WITH receipt AS (
            INSERT INTO whatsapp.message_receipts (id, wamid, status, recipient_id, occurred_at, error_code, created_at)
            VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6})
            ON CONFLICT (wamid, status) DO NOTHING
            RETURNING 1
        ), outbound AS (
            UPDATE whatsapp.outbound_messages o
            SET status = {2}, status_at = {4}, error_code = COALESCE({5}, o.error_code)
            WHERE o.wamid = {1} AND {{RankSql("o.status")}} < {7}
        ), reminder AS (
            UPDATE whatsapp.reminder_deliveries d
            SET receipt_status = {2}, receipt_at = {4}, receipt_error_code = COALESCE({5}, d.receipt_error_code)
            WHERE d.wamid = {1} AND {{RankSql("d.receipt_status")}} < {7}
        )
        SELECT count(*)::int AS "Value" FROM receipt
        """;

    private static readonly string ReconcileOutboundSql = $$"""
        UPDATE whatsapp.outbound_messages o
        SET status = b.status, status_at = b.occurred_at, error_code = COALESCE(b.error_code, o.error_code)
        FROM ({{BestReceiptSql}}) b
        WHERE o.wamid = {0} AND {{RankSql("o.status")}} < {{RankSql("b.status")}}
        """;

    private static readonly string ReconcileReminderSql = $$"""
        UPDATE whatsapp.reminder_deliveries d
        SET receipt_status = b.status, receipt_at = b.occurred_at,
            receipt_error_code = COALESCE(b.error_code, d.receipt_error_code)
        FROM ({{BestReceiptSql}}) b
        WHERE d.wamid = {0} AND {{RankSql("d.receipt_status")}} < {{RankSql("b.status")}}
        """;

    /// <summary>
    /// Guarda el recibo (idempotente por wamid+estado) y avanza, en la misma sentencia, el estado
    /// de los salientes locales con ese wamid. Devuelve true si el recibo era nuevo.
    /// </summary>
    public static async Task<bool> RecordAsync(
        WhatsAppDbContext db,
        string wamid,
        MessageStatus status,
        string? recipientId,
        DateTimeOffset occurredAt,
        string? errorCode,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        var inserted = await db
            .Database.SqlQueryRaw<int>(
                RecordSql,
                Guid.NewGuid(),
                wamid,
                status.ToString(),
                (object?)recipientId ?? DBNull.Value,
                occurredAt,
                (object?)Truncate(errorCode) ?? DBNull.Value,
                now,
                MessageStatusOrder.Rank(status)
            )
            .ToListAsync(ct);
        return inserted.Count > 0 && inserted[0] > 0;
    }

    /// <summary>Aplica a <c>outbound_messages</c> el recibo más avanzado ya persistido para el wamid.</summary>
    public static Task<int> ReconcileOutboundAsync(
        WhatsAppDbContext db,
        string wamid,
        CancellationToken ct
    ) => db.Database.ExecuteSqlRawAsync(ReconcileOutboundSql, [wamid], ct);

    /// <summary>Aplica a <c>reminder_deliveries</c> el recibo más avanzado ya persistido para el wamid.</summary>
    public static Task<int> ReconcileReminderAsync(
        WhatsAppDbContext db,
        string wamid,
        CancellationToken ct
    ) => db.Database.ExecuteSqlRawAsync(ReconcileReminderSql, [wamid], ct);

    /// <summary>Wamid recortado para logs: identifica el mensaje sin volcarlo entero.</summary>
    public static string MaskWamid(string? wamid) =>
        string.IsNullOrEmpty(wamid) ? ""
        : wamid.Length <= 12 ? "***"
        : "…" + wamid[^8..];

    public static string? Truncate(string? errorCode) =>
        errorCode is { Length: > MaxErrorCodeLength } ? errorCode[..MaxErrorCodeLength] : errorCode;
}
