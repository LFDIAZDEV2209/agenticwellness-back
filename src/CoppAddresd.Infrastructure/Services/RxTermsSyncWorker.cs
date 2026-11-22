using System.Globalization;
using System.IO.Compression;
using System.Net;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace CoppAddresd.Infrastructure.Services;

/// <summary>Actualiza la referencia pública. Las búsquedas clínicas nunca salen de PostgreSQL.</summary>
public sealed class RxTermsSyncWorker(
    IServiceScopeFactory scopes, IHttpClientFactory clients, IConfiguration configuration,
    TimeProvider clock, ILogger<RxTermsSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("Medications:Enabled") ||
            !configuration.GetValue("Medications:RxTermsSyncEnabled", true))
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromHours(24), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await SyncAsync(db, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo sincronizar RxTerms; se conserva el catálogo anterior.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncAsync(AppDbContext db, CancellationToken ct)
    {
        var current = await LatestReleaseAsync(db, ct);
        var now = clock.GetUtcNow();
        using var client = clients.CreateClient("RxTerms");
        // NLM publica después del inicio de mes y ocasionalmente omite una edición.
        for (var monthsAgo = 0; monthsAgo < 3; monthsAgo++)
        {
            var release = now.AddMonths(-monthsAgo).ToString("yyyyMM", CultureInfo.InvariantCulture);
            if (IsPublished(current, release))
                return;
            using var response = await client.GetAsync($"RxTerms{release}.zip", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                continue;
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var products = await ReadProductsAsync(stream, release, ct);
            await PublishAsync(db, release, products, ct);
            return;
        }

        logger.LogWarning("No se encontró una edición reciente de RxTerms.");
    }

    private static async Task<IReadOnlyList<RxTermsProduct>> ReadProductsAsync(Stream stream, string release, CancellationToken ct)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var source = archive.GetEntry($"RxTerms{release}.txt");
        if (source is null || source.Length > 20 * 1024 * 1024)
            throw new InvalidDataException("Archivo principal de RxTerms ausente o fuera de tamaño.");

        using var reader = new StreamReader(source.Open());
        var header = (await reader.ReadLineAsync(ct) ?? "").Split('|');
        int Index(string field)
        {
            var index = Array.IndexOf(header, field);
            return index >= 0 ? index : throw new InvalidDataException($"Falta la columna {field} de RxTerms.");
        }
        int id = Index("RXCUI"), full = Index("FULL_NAME"), generic = Index("FULL_GENERIC_NAME"),
            brand = Index("BRAND_NAME"), display = Index("DISPLAY_NAME"),
            synonym = Index("DISPLAY_NAME_SYNONYM"), strength = Index("STRENGTH"),
            route = Index("ROUTE"), doseForm = Index("RXN_DOSE_FORM"), unitForm = Index("NEW_DOSE_FORM"),
            retired = Index("IS_RETIRED"), suppressed = Index("SUPPRESS_FOR");

        var products = new List<RxTermsProduct>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0) continue;
            var fields = line.Split('|');
            if (fields.Length != header.Length)
                throw new InvalidDataException("Fila de RxTerms incompleta.");
            if (fields[suppressed].Length > 0 || fields[retired].Length > 0)
                continue;
            var code = fields[id];
            var name = fields[full].Trim();
            if (code.Length is < 1 or > 18 || code.Any(c => !char.IsAsciiDigit(c)) || name.Length == 0 || !seen.Add(code))
                throw new InvalidDataException("RxCUI repetido o presentación de RxTerms inválida.");
            var routeName = fields[route].Trim();
            products.Add(new()
            {
                RxCui = code,
                Name = name,
                SearchText = string.Join(' ', code, name, fields[generic], fields[brand], fields[display], fields[synonym], fields[strength]),
                Strength = NonEmpty(fields[strength]),
                DoseForm = NonEmpty(fields[doseForm]),
                Route = NormalizeRoute(routeName),
                DoseUnit = DoseUnit(routeName, fields[unitForm].Trim()),
                Release = release,
                IsActive = true,
            });
        }
        if (products.Count < 1000)
            throw new InvalidDataException("La edición de RxTerms está incompleta.");
        return products;
    }

    private static string? NonEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeRoute(string route)
    {
        if (route is "Oral Pill" or "Oral Liquid" or "Chewable" or "Effervescent")
            return "Oral";

        // Pack describe el envase y puede agrupar más de una presentación.
        return route is "" or "Pack" ? null : route;
    }

    private static string? DoseUnit(string route, string form)
    {
        // Unidades sugeridas de presentaciones claras; nunca se usa la concentración ni el envase como dosis.
        if (route == "Oral Liquid") return "mL";
        if (form == "Tab" || form.EndsWith(" Tab", StringComparison.Ordinal)) return "tablet";
        if (form == "Cap" || form.EndsWith(" Cap", StringComparison.Ordinal)) return "capsule";
        if (form == "Lozenge") return "lozenge";
        if (form == "Suppository") return "suppository";
        if (route == "Transdermal" && form.EndsWith(" Patch", StringComparison.Ordinal)) return "patch";
        return null;
    }

    private async Task PublishAsync(AppDbContext db, string release, IReadOnlyList<RxTermsProduct> products, CancellationToken cancellationToken)
    {
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('app.rxterms_products.sync'))", ct);
            if (IsPublished(await LatestReleaseAsync(db, ct), release))
                return;

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TEMP TABLE rxterms_stage (LIKE app.rxterms_products) ON COMMIT DROP
                """, ct);
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using (var writer = await connection.BeginBinaryImportAsync("""
                COPY rxterms_stage (rx_cui, name, search_text, strength, dose_form, route, dose_unit, release, is_active)
                FROM STDIN (FORMAT BINARY)
                """, ct))
            {
                foreach (var product in products)
                {
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(product.RxCui, ct);
                    await writer.WriteAsync(product.Name, ct);
                    await writer.WriteAsync(product.SearchText, ct);
                    await writer.WriteAsync(product.Strength, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(product.DoseForm, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(product.Route, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(product.DoseUnit, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(product.Release, ct);
                    await writer.WriteAsync(product.IsActive, ct);
                }
                await writer.CompleteAsync(ct);
            }

            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO app.rxterms_products (rx_cui, name, search_text, strength, dose_form, route, dose_unit, release, is_active)
                SELECT rx_cui, name, search_text, strength, dose_form, route, dose_unit, release, is_active FROM rxterms_stage
                ON CONFLICT (rx_cui) DO UPDATE
                SET name = EXCLUDED.name, search_text = EXCLUDED.search_text,
                    strength = EXCLUDED.strength, dose_form = EXCLUDED.dose_form,
                    route = EXCLUDED.route, dose_unit = EXCLUDED.dose_unit,
                    release = EXCLUDED.release, is_active = true
                """, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE app.rxterms_products SET is_active = false
                WHERE is_active AND release <> {release}
                """, ct);

            await transaction.CommitAsync(ct);
            logger.LogInformation("RxTerms {Release}: {Count} presentaciones activas.", release, products.Count);
        }, cancellationToken);
    }

    private static Task<PublishedRelease?> LatestReleaseAsync(AppDbContext db, CancellationToken ct) =>
        db.RxTermsProducts.Where(x => x.IsActive).OrderByDescending(x => x.Release)
            .ThenByDescending(x => x.DoseForm != null)
            .Select(x => new PublishedRelease(x.Release, x.DoseForm != null)).FirstOrDefaultAsync(ct);

    // Volver a descargar una vez la edición instalada por la versión anterior del importador.
    private static bool IsPublished(PublishedRelease? current, string release) =>
        current is not null && (string.CompareOrdinal(current.Release, release) > 0
            || (current.Release == release && current.HasMetadata));

    private sealed record PublishedRelease(string Release, bool HasMetadata);
}
