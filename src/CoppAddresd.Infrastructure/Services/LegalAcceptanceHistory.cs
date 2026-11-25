using System.Globalization;
using System.Text;
using System.Text.Json;
using CoppAddresd.Application.Features.LegalDocuments;
using CoppAddresd.Application.Interfaces;
using CoppAddresd.Domain.Enums;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Infrastructure.Services;

public sealed record LegalAcceptanceFilter(Guid? AccountId, string? DocumentCode, (int Major, int Minor)? Version,
    DateTimeOffset? From, DateTimeOffset? Before);

public sealed record LegalAcceptanceHistoryRow(Guid ReceiptId, Guid AccountId, string Scope, string DocumentCode,
    string DocumentTitle, Guid VersionId, string Version, string Locale, DateTime AcceptedAt,
    string? Ip, string? UserAgent);

public sealed record LegalAcceptanceHistoryPage(IReadOnlyList<LegalAcceptanceHistoryRow> Items,
    int Total, int Page, int PageSize);

public sealed class LegalAcceptanceHistory(AppDbContext db, IPatientScopeGuard guard, IPatientScopeStore scopeStore)
{
    private const int ExportLimit = 5000;

    public async Task<LegalAcceptanceHistoryPage> ListAsync(LegalAcceptanceFilter filter, int page, int pageSize,
        Guid? clinicId, Guid? organizationId, string permission, CancellationToken ct)
    {
        var query = await ScopedQueryAsync(filter, clinicId, organizationId, permission, ct);
        var total = await query.CountAsync(ct);
        var offset = ((long)page - 1) * pageSize;
        var rows = offset >= total ? [] : await query.Skip((int)offset).Take(pageSize).ToArrayAsync(ct);
        return new(rows.Select(ToHistoryRow).ToArray(), total, page, pageSize);
    }

    public async Task<byte[]?> ExportAsync(LegalAcceptanceFilter filter, Guid actorId,
        Guid? clinicId, Guid? organizationId, string permission, CancellationToken ct)
    {
        var query = await ScopedQueryAsync(filter, clinicId, organizationId, permission, ct);
        var rows = await query.Take(ExportLimit + 1).ToArrayAsync(ct);
        if (rows.Length > ExportLimit) return null;

        var csv = new StringBuilder("\uFEFFreceiptId,accountId,scope,documentCode,documentTitle,versionId,version,locale,acceptedAtUtc,ip,userAgent\r\n");
        foreach (var row in rows.Select(ToHistoryRow))
        {
            csv.AppendJoin(',', CsvCell(row.ReceiptId.ToString()), CsvCell(row.AccountId.ToString()), CsvCell(row.Scope),
                CsvCell(row.DocumentCode), CsvCell(row.DocumentTitle), CsvCell(row.VersionId.ToString()),
                CsvCell(row.Version), CsvCell(row.Locale), CsvCell(row.AcceptedAt.ToString("O", CultureInfo.InvariantCulture)),
                CsvCell(row.Ip), CsvCell(row.UserAgent));
            csv.Append("\r\n");
        }

        // El evento registra quién exportó y cuántas filas. Los recibos y filtros personales quedan en el archivo.
        var metadata = JsonSerializer.Serialize(new { rowCount = rows.Length, clinicId, organizationId });
        var exportId = Guid.NewGuid().ToString();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO audit.activity_logs (action, schema_name, table_name, record_id, actor_type, user_id, metadata)
            VALUES ({nameof(AuditAction.LegalAcceptanceHistoryExport)}, 'app', 'legal_document_acceptances',
                {exportId}, 'USER', {actorId}, {metadata}::jsonb)
            """, ct);
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private async Task<IQueryable<ReceiptData>> ScopedQueryAsync(LegalAcceptanceFilter filter,
        Guid? clinicId, Guid? organizationId, string permission, CancellationToken ct)
    {
        var access = await guard.GetFilterAsync(permission, ct);
        var receipts = db.LegalDocumentAcceptances.AsNoTracking();
        if (!access.IsAdmin || clinicId.HasValue || organizationId.HasValue)
        {
            var visiblePatientIds = scopeStore.VisiblePatients(access.ProfessionalId, access.IsAdmin);
            var profiles = db.PatientProfiles.AsNoTracking().Where(patient => visiblePatientIds.Contains(patient.Id));
            if (clinicId is { } clinic)
                profiles = profiles.Where(patient => patient.ClinicId == clinic);
            else if (organizationId is { } organization)
                profiles = profiles.Where(patient => db.Clinics.Any(c => c.Id == patient.ClinicId && c.OrganizationId == organization));
            receipts = receipts.Where(receipt => profiles.Any(patient => patient.UserId == receipt.UserId));
        }

        if (filter.AccountId is { } accountId) receipts = receipts.Where(receipt => receipt.UserId == accountId);
        if (filter.From is { } from) receipts = receipts.Where(receipt => receipt.AcceptedAt >= from.UtcDateTime);
        if (filter.Before is { } before) receipts = receipts.Where(receipt => receipt.AcceptedAt < before.UtcDateTime);

        var versions = db.LegalDocumentVersions.AsNoTracking();
        if (filter.Version is { } requested)
            versions = versions.Where(version => version.Major == requested.Major && version.Minor == requested.Minor);

        return from receipt in receipts
            join version in versions on receipt.LegalDocumentVersionId equals version.Id
            join document in db.LegalDocuments.AsNoTracking() on version.DocumentId equals document.Id
            where (filter.DocumentCode == null || document.Code == filter.DocumentCode)
                && (receipt.Scope == AccountLegalScopes.Terms || receipt.Scope == AccountLegalScopes.Privacy
                    || ((receipt.Locale == "es" || receipt.Locale == "en") && document.Code == receipt.Scope
                        && (receipt.Scope == "paid-demo-terms-" + receipt.Locale
                            || receipt.Scope == "paid-demo-privacy-" + receipt.Locale)))
            orderby receipt.AcceptedAt descending, receipt.Id descending
            select new ReceiptData(receipt.Id, receipt.UserId, receipt.Scope, document.Code, document.Title,
                version.Id, version.Major, version.Minor, receipt.Locale, receipt.AcceptedAt, receipt.DisclosureSnapshot);
    }

    private static LegalAcceptanceHistoryRow ToHistoryRow(ReceiptData receipt)
    {
        using var metadata = JsonDocument.Parse(receipt.DisclosureSnapshot);
        var snapshot = metadata.RootElement;
        return new(receipt.ReceiptId, receipt.AccountId, receipt.Scope, receipt.DocumentCode, receipt.DocumentTitle,
            receipt.VersionId, FormattableString.Invariant($"{receipt.Major}.{receipt.Minor}"), receipt.Locale, receipt.AcceptedAt,
            StringProperty(snapshot, "ip"), StringProperty(snapshot, "userAgent"));
    }

    private static string? StringProperty(JsonElement snapshot, string property) => snapshot.ValueKind == JsonValueKind.Object
        && snapshot.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string CsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var trimmed = value.AsSpan().TrimStart();
        if (value[0] is '\t' or '\r' or '\n' || (!trimmed.IsEmpty && trimmed[0] is '=' or '+' or '-' or '@'))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private sealed record ReceiptData(Guid ReceiptId, Guid AccountId, string Scope, string DocumentCode,
        string DocumentTitle, Guid VersionId, int Major, int Minor, string Locale, DateTime AcceptedAt, string DisclosureSnapshot);
}
