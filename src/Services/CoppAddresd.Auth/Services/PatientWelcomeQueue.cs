using System.Net.Mail;
using CoppAddresd.Auth.Data;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Auth.Services;

/// <summary>Intercambia identificadores de alta ERP en la BD compartida, sin copiar datos de contacto a la cola.</summary>
internal static class PatientWelcomeQueue
{
    public static async Task TransferPendingAsync(AuthDbContext db, CancellationToken ct)
    {
        if (!await HasRequestTableAsync(db, ct))
            return;

        // Reclamar lotes entre réplicas y transferir en la misma sentencia.
        // Si falla el insert, la solicitud sigue pendiente en ERP.
        await db.Database.ExecuteSqlRawAsync("""
            WITH pending AS (
                SELECT patient_id
                FROM erp.patient_welcome_requests
                WHERE transferred_at IS NULL
                ORDER BY requested_at, patient_id
                LIMIT 20
                FOR UPDATE SKIP LOCKED
            ),
            transferred AS (
                UPDATE erp.patient_welcome_requests AS request
                SET transferred_at = now()
                FROM pending
                WHERE request.patient_id = pending.patient_id
                RETURNING request.patient_id, request.requested_at
            )
            INSERT INTO auth."WelcomeEmailDeliveries"
                ("Id", "PatientId", "DeduplicationKey", "CreatedAt", "ReadyAt", "Status", "Attempts", "NextAttemptAt")
            SELECT gen_random_uuid(), patient_id, 'patient-welcome:' || patient_id,
                requested_at, now(), 'Pending', 0, now()
            FROM transferred
            WHERE NOT EXISTS (
                -- Durante un despliegue, una réplica anterior de Auth aún puede haber
                -- encolado la bienvenida de cuenta al verificar OTP tras el alta.
                SELECT 1
                FROM app.patient_profiles AS patient
                JOIN auth."WelcomeEmailDeliveries" AS account
                    ON account."DeduplicationKey" = 'welcome:' || patient.user_id
                JOIN auth."UserApplications" AS access ON access."UserId" = patient.user_id
                JOIN auth."Applications" AS app ON app."Id" = access."ApplicationId"
                WHERE patient.id = transferred.patient_id
                    AND account."CreatedAt" >= transferred.requested_at
                    AND account."Status" <> 'Skipped'
                    AND app."Code" = 'app' AND app."IsActive" AND NOT access."IsSuspended"
            )
            ON CONFLICT ("DeduplicationKey") DO NOTHING
            """, ct);
    }

    public static async Task<bool> OwnsWelcomeAsync(AuthDbContext db, Guid patientId, CancellationToken ct)
    {
        if (!await HasRequestTableAsync(db, ct))
            return false;

        // También detecta solicitudes aún no transferidas si el paciente verifica OTP enseguida.
        return await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1
                FROM erp.patient_welcome_requests AS request
                WHERE request.patient_id = {patientId}
                    AND NOT EXISTS (
                        SELECT 1 FROM auth."WelcomeEmailDeliveries" AS delivery
                        WHERE delivery."PatientId" = request.patient_id AND delivery."Status" = 'Skipped'
                    )
            ) AS "Value"
            """).SingleAsync(ct);
    }

    public static async Task<WelcomeEmailContent?> ResolveAsync(AuthDbContext db, Guid patientId, CancellationToken ct)
    {
        var patient = await db.Database.SqlQuery<PatientContact>($"""
            SELECT first_name AS "FirstName", email AS "Email", user_id AS "UserId"
            FROM app.patient_profiles
            WHERE id = {patientId}
                AND deleted_at IS NULL
                AND status IS DISTINCT FROM 'Inactivo'
                AND NULLIF(btrim(document_number), '') IS NOT NULL
            """).SingleOrDefaultAsync(ct);
        if (patient?.Email is not string email ||
            !MailAddress.TryCreate(email, out var address) || address.Address != email)
            return null;

        var app = await db.Applications.AsNoTracking().SingleOrDefaultAsync(a => a.Code == "app" && a.IsActive, ct);
        if (app is null)
            return null;

        // Respeta las suspensiones de una cuenta vinculada o ya registrada con ese correo.
        var normalizedEmail = email.Normalize().ToUpperInvariant();
        var accounts = db.Users.AsNoTracking();
        var account = await (patient.UserId is Guid userId
                ? accounts.Where(x => x.Id == userId)
                : accounts.Where(x => x.NormalizedEmail == normalizedEmail))
            .Select(x => new { x.Id, x.IsActive })
            .FirstOrDefaultAsync(ct);
        if (account is not null && (!account.IsActive || await db.UserApplications.AnyAsync(
            x => x.UserId == account.Id && x.ApplicationId == app.Id && x.IsSuspended, ct)))
            return null;

        var lang = account is null ? null : await db.UserPreferences.AsNoTracking()
            .Where(x => x.UserId == account.Id).Select(x => x.Lang).FirstOrDefaultAsync(ct);
        return new(email, patient.FirstName, lang, AppAccess: true, ErpAccess: false, [], [], IsPatientOnboarding: true);
    }

    private static async Task<bool> HasRequestTableAsync(AuthDbContext db, CancellationToken ct) =>
        db.Database.IsNpgsql() && await db.Database.SqlQueryRaw<bool>(
            """
            SELECT to_regclass('erp.patient_welcome_requests') IS NOT NULL AS "Value"
            """).SingleAsync(ct);

    private sealed record PatientContact(string FirstName, string? Email, Guid? UserId);
}
