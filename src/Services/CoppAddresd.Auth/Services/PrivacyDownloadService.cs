using System.Data;
using System.Text;
using System.Text.Json;
using CoppAddresd.Auth.Data;
using Microsoft.EntityFrameworkCore;

namespace CoppAddresd.Auth.Services;

public sealed record PreparedPrivacyDownload(string Format, string Scope, string Content);

public sealed class PrivacyDownloadService(AuthDbContext db)
{
    private const int MaximumRowsPerSource = 5000;
    private const string Scope = "profile_and_vitals";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<PreparedPrivacyDownload?> BuildAsync(Guid userId, string format, CancellationToken ct)
    {
        // Una instantánea para las tres lecturas. Proyecciones explícitas:
        // no salen notas, contactos de terceros, claves de almacenamiento ni credenciales.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var profileRows = await db.Database.SqlQuery<string>(
            $"""
            SELECT jsonb_build_object(
                'id', id, 'firstName', first_name, 'middleName', middle_name,
                'lastName', last_name, 'dateOfBirth', date_of_birth, 'email', email,
                'phoneCountryCode', phone_country_code, 'phoneNumber', phone_number,
                'gender', gender, 'timezone', timezone
            )::text AS "Value"
            FROM app.patient_profiles
            WHERE user_id = {userId}
            """).ToListAsync(ct);
        if (profileRows.Count != 1)
            return null;

        var legacy = await db.Database.SqlQuery<string>(
            $"""
            SELECT jsonb_build_object(
                'id', v.id, 'measuredAt', v.measured_at, 'systolic', v.systolic,
                'diastolic', v.diastolic, 'heartRate', v.heart_rate,
                'temperatureC', v.temperature_c, 'o2Saturation', v.o2_saturation,
                'heightCm', v.height_cm, 'weightKg', v.weight_kg
            )::text AS "Value"
            FROM app.vital_signs v JOIN app.patient_profiles p ON p.id = v.patient_id
            WHERE p.user_id = {userId}
            ORDER BY v.measured_at, v.id
            LIMIT {MaximumRowsPerSource + 1}
            """).ToListAsync(ct);

        var current = await db.Database.SqlQuery<string>(
            $"""
            SELECT jsonb_build_object(
                'id', c.id, 'metric', m.code, 'value', c.value, 'unit', u.code,
                'observedAt', c.observed_at, 'recordedAt', c.recorded_at, 'status', c.status
            )::text AS "Value"
            FROM app.clinical_measurements c
            JOIN app.patient_profiles p ON p.id = c.patient_id
            JOIN app.measurement_metrics m ON m.id = c.metric_id
            JOIN app.unit_of_measures u ON u.id = c.unit_id
            WHERE p.user_id = {userId} AND m.category = 'vital'
            ORDER BY c.observed_at, c.id
            LIMIT {MaximumRowsPerSource + 1}
            """).ToListAsync(ct);

        if (legacy.Count > MaximumRowsPerSource || current.Count > MaximumRowsPerSource)
            return null; // Nunca servir una copia silenciosamente truncada.

        await tx.CommitAsync(ct);
        ct.ThrowIfCancellationRequested();
        var data = new
        {
            scope = Scope,
            version = 1,
            preparedAt = DateTime.UtcNow,
            profile = Parse(profileRows[0]),
            legacyVitals = legacy.Select(Parse).ToArray(),
            clinicalVitals = current.Select(Parse).ToArray()
        };
        if (format == "json")
            return new(format, Scope, JsonSerializer.Serialize(data, JsonOptions));

        var csv = new StringBuilder("dataset,record_id,field,value\r\n");
        CsvRow(csv, "metadata", "", "scope", Scope);
        CsvRow(csv, "metadata", "", "version", "1");
        CsvRow(csv, "metadata", "", "preparedAt", data.preparedAt.ToString("O"));
        WriteRecord(csv, "profile", data.profile);
        foreach (var row in data.legacyVitals)
            WriteRecord(csv, "legacyVitals", row);
        foreach (var row in data.clinicalVitals)
            WriteRecord(csv, "clinicalVitals", row);
        return new(format, Scope, csv.ToString());
    }

    private static JsonElement Parse(string row)
    {
        using var document = JsonDocument.Parse(row);
        return document.RootElement.Clone();
    }

    private static void WriteRecord(StringBuilder csv, string dataset, JsonElement row)
    {
        var id = row.GetProperty("id").GetString()!;
        foreach (var field in row.EnumerateObject())
        {
            if (field.Name == "id")
                continue;
            var value = field.Value.ValueKind switch
            {
                JsonValueKind.Null => "",
                JsonValueKind.String => SafeSpreadsheetText(field.Value.GetString()!),
                _ => field.Value.GetRawText()
            };
            CsvRow(csv, dataset, id, field.Name, value);
        }
    }

    internal static string SafeSpreadsheetText(string value) =>
        value.TrimStart().AsSpan() is ['=' or '+' or '-' or '@', ..]
            || value.AsSpan() is ['\t' or '\r' or '\n', ..]
                ? "'" + value
                : value;

    internal static void CsvRow(StringBuilder csv, string dataset, string id, string field, string value)
    {
        static string Cell(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
        csv.Append(Cell(dataset)).Append(',').Append(Cell(id)).Append(',')
            .Append(Cell(field)).Append(',').Append(Cell(value)).Append("\r\n");
    }
}
