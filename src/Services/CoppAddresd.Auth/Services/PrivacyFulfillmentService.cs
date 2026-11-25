using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoppAddresd.Auth.Data;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Auth.Services;

public sealed record ExportDataset(string Category, string Status, string EvidenceRef, JsonElement[] Records);
public sealed record ReviewedExport(Guid UserId, ExportDataset[] Datasets);
public sealed record ReadyPrivacyExport(Guid RequestId, string Format, DateTime ExpiresAt);
public enum PublishPrivacyResult { Published, InvalidManifest, MissingRequest, AlreadyAvailable }

public sealed class PrivacyFulfillmentService(AuthDbContext db, IConfiguration configuration)
{
    private const string Scope = "reviewed_full_copy";
    private static readonly string[] Categories = ["profile", "wellness", "vitals", "clinical", "ai", "service_logs", "audit", "archives"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool Enabled => configuration.GetValue<bool>("PrivacyRequests:Enabled") &&
        configuration.GetValue<bool>("PrivacyRequests:FulfillmentEnabled");

    public async Task<PublishPrivacyResult> PublishAsync(Guid requestId, Guid operatorId, ReviewedExport bundle, CancellationToken ct)
    {
        if (!IsValid(bundle))
            return PublishPrivacyResult.InvalidManifest;
        var receipt = await db.PrivacyRequests.Where(x => x.Id == requestId && x.UserId == bundle.UserId &&
                x.Kind == PrivacyRequestService.Export && x.ClosedAt == null)
            .Select(x => new { x.Format }).SingleOrDefaultAsync(ct);
        if (receipt?.Format is not ("json" or "csv"))
            return PublishPrivacyResult.MissingRequest;

        var now = DateTime.UtcNow;
        var expiry = now.AddHours(Math.Clamp(configuration.GetValue("PrivacyRequests:DownloadHours", 24), 1, 168));
        var content = Render(bundle, receipt.Format, now);
        var protectedContent = Encrypt(content, requestId, bundle.UserId);
        // Un operador no sobrescribe una copia disponible. Puede preparar otra si
        // venció sin entrega, conservando actor y momento de la publicación actual.
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO auth."PrivacyExportArtifacts"
                ("RequestId", "PublishedBy", "PublishedAt", "ExpiresAt", "ProtectedContent")
            SELECT {requestId}, {operatorId}, {now}, {expiry}, {protectedContent}
            FROM auth."PrivacyRequests" r WHERE r."Id" = {requestId} AND r."ClosedAt" IS NULL
            ON CONFLICT ("RequestId") DO UPDATE SET
                "PublishedBy" = EXCLUDED."PublishedBy", "PublishedAt" = EXCLUDED."PublishedAt",
                "ExpiresAt" = EXCLUDED."ExpiresAt", "ProtectedContent" = EXCLUDED."ProtectedContent"
            WHERE auth."PrivacyExportArtifacts"."ExpiresAt" <= {now}
                AND auth."PrivacyExportArtifacts"."DeliveredAt" IS NULL
            """, ct);
        return changed == 1 ? PublishPrivacyResult.Published : PublishPrivacyResult.AlreadyAvailable;
    }

    public Task<ReadyPrivacyExport?> GetReadyAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return (from a in db.PrivacyExportArtifacts
                join r in db.PrivacyRequests on a.RequestId equals r.Id
                where r.UserId == userId && a.ExpiresAt > now && a.ProtectedContent != null
                orderby a.PublishedAt descending, a.RequestId descending
                select new ReadyPrivacyExport(a.RequestId, r.Format!, a.ExpiresAt))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<PreparedPrivacyDownload?> DeliverAsync(Guid userId, Guid requestId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var found = await (from a in db.PrivacyExportArtifacts
            join r in db.PrivacyRequests on a.RequestId equals r.Id
            where a.RequestId == requestId && r.UserId == userId && a.ExpiresAt > now && a.ProtectedContent != null
            select new { r.Format, a.ProtectedContent, a.PublishedAt }).SingleOrDefaultAsync(ct);
        if (found is null)
            return null;

        var content = Decrypt(found.ProtectedContent!, requestId, userId);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var deliveredAt = DateTime.UtcNow;
        var changed = await db.PrivacyExportArtifacts
            .Where(a => a.RequestId == requestId && a.PublishedAt == found.PublishedAt && a.ExpiresAt > deliveredAt && a.ProtectedContent != null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeliveredAt, a => a.DeliveredAt ?? deliveredAt), ct);
        if (changed != 1)
            return null;
        await db.PrivacyRequests.Where(r => r.Id == requestId && r.UserId == userId && r.ClosedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ClosedAt, deliveredAt), ct);
        await tx.CommitAsync(ct);
        return new(found.Format!, Scope, content);
    }

    public Task<int> ClearExpiredAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return db.PrivacyExportArtifacts.Where(a => a.ExpiresAt <= now && a.ProtectedContent != null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ProtectedContent, (byte[]?)null), ct);
    }

    private static bool IsValid(ReviewedExport bundle) =>
        bundle.UserId != Guid.Empty && bundle.Datasets is not null && bundle.Datasets.Length == Categories.Length &&
        Categories.All(category => bundle.Datasets.Count(d => d?.Category == category) == 1) &&
        bundle.Datasets.All(d => !string.IsNullOrWhiteSpace(d.EvidenceRef) && d.EvidenceRef.Length <= 128 &&
            d.Records is not null && (d.Status switch
            {
                "included" => d.Records.Length > 0 && d.Records.All(r => r.ValueKind == JsonValueKind.Object && HasNoSecrets(r)),
                "no_data" or "excluded" => d.Records.Length == 0,
                _ => false
            }));

    private static bool HasNoSecrets(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().All(p =>
            !KnownSecrets.Contains(new string(p.Name.Where(char.IsLetterOrDigit).ToArray())) && HasNoSecrets(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(HasNoSecrets),
        _ => true
    };

    private static readonly HashSet<string> KnownSecrets = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwordHash", "accessToken", "refreshToken", "securityStamp", "apiKey",
        "secret", "secretHash", "privateKey", "otp", "otpCode", "authorization", "cookie"
    };

    private static string Render(ReviewedExport bundle, string format, DateTime now)
    {
        // La referencia de evidencia se conserva en la entrega revisada, no se
        // utiliza como enlace en la app. Solo debe contener identificadores opacos.
        if (format == "json")
            return JsonSerializer.Serialize(new { scope = Scope, version = 1, preparedAt = now, bundle.Datasets }, JsonOptions);

        var csv = new StringBuilder("dataset,record_id,field,value\r\n");
        PrivacyDownloadService.CsvRow(csv, "metadata", "", "scope", Scope);
        PrivacyDownloadService.CsvRow(csv, "metadata", "", "preparedAt", now.ToString("O"));
        foreach (var dataset in bundle.Datasets)
        {
            PrivacyDownloadService.CsvRow(csv, dataset.Category, "", "coverage", dataset.Status);
            PrivacyDownloadService.CsvRow(csv, dataset.Category, "", "evidence",
                PrivacyDownloadService.SafeSpreadsheetText(dataset.EvidenceRef));
            for (var i = 0; i < dataset.Records.Length; i++)
                foreach (var field in dataset.Records[i].EnumerateObject())
                    PrivacyDownloadService.CsvRow(csv, dataset.Category, (i + 1).ToString(),
                        PrivacyDownloadService.SafeSpreadsheetText(field.Name), field.Value.ValueKind == JsonValueKind.String
                            ? PrivacyDownloadService.SafeSpreadsheetText(field.Value.GetString()!)
                            : field.Value.GetRawText());
        }
        return csv.ToString();
    }

    private AesGcm Cipher()
    {
        // Clave de 32 bytes procedente del gestor de secretos, no del JWT.
        var key = Convert.FromHexString(configuration["PrivacyRequests:ArtifactKey"] ?? "");
        try
        {
            if (key.Length != 32)
                throw new InvalidOperationException("Privacy export artifact key is not configured.");
            return new AesGcm(key, 16);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private byte[] Encrypt(string content, Guid requestId, Guid subject)
    {
        var plain = Encoding.UTF8.GetBytes(content);
        var output = new byte[28 + plain.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        try
        {
            using var aes = Cipher();
            aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(28), output.AsSpan(12, 16), Bind(requestId, subject));
            return output;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private string Decrypt(byte[] content, Guid requestId, Guid subject)
    {
        var plain = new byte[content.Length - 28];
        try
        {
            using var aes = Cipher();
            aes.Decrypt(content.AsSpan(0, 12), content.AsSpan(28), content.AsSpan(12, 16), plain, Bind(requestId, subject));
            return Encoding.UTF8.GetString(plain);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] Bind(Guid requestId, Guid subject) => Encoding.UTF8.GetBytes($"{requestId:N}/{subject:N}");
}
