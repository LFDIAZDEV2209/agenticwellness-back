using System.Text;
using CoppAddresd.Application.Common.Storage;
using CoppAddresd.Application.Interfaces;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace CoppAddresd.Infrastructure.Services;

/// <summary>Extrae texto de originales autorizados con límites de tamaño y páginas.</summary>
public sealed class VitaDocumentTextReader(IObjectStorageService storage)
{
    private const int MaxBytes = 16 * 1024 * 1024;
    private const int MaxCharacters = 100_000;
    private const int MaxPages = 100;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<string?> ReadAsync(Guid patientId, Guid documentId, string key, long? size, CancellationToken ct)
    {
        var archive = PatientDocumentStorageKeys.IsArchiveOriginal(key, patientId, documentId)
            || (PatientDocumentStorageKeys.TryParseVitaAttachment(key, out var owner, out var id)
                && owner == patientId && id == documentId);
        if (!archive && !PatientDocumentStorageKeys.IsUploadKey(key, patientId)) return null;
        var extension = Path.GetExtension(key).ToLowerInvariant();
        if (size > MaxBytes || extension is not (".pdf" or ".txt" or ".md" or ".csv")) return null;
        await using var original = archive
            ? await storage.GetDocumentArchiveObjectAsync(key, ct)
            : await storage.GetObjectAsync(key, ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await original.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + read > MaxBytes) return null;
            bytes.Write(buffer, 0, read);
        }
        if (bytes.Length == 0) return null;

        bytes.Position = 0;
        return extension == ".pdf" ? ReadPdf(bytes, ct) : ReadUtf8(bytes);
    }

    private static string? ReadPdf(MemoryStream bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            var text = new StringBuilder();
            for (var page = 1; page <= Math.Min(pdf.NumberOfPages, MaxPages) && text.Length < MaxCharacters; page++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var word in pdf.GetPage(page).GetWords())
                {
                    var remaining = MaxCharacters - text.Length;
                    if (remaining == 0) break;
                    text.Append(word.Text.AsSpan(0, Math.Min(word.Text.Length, remaining)));
                    if (text.Length < MaxCharacters) text.Append(' ');
                }
                if (text.Length < MaxCharacters) text.Append('\n');
            }
            return Normalize(text.ToString());
        }
        catch (Exception error) when (error is PdfDocumentEncryptedException or PdfDocumentFormatException) { return null; }
    }

    private static string? ReadUtf8(MemoryStream bytes)
    {
        try
        {
            var content = Utf8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
            return content.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t'))
                ? null : Normalize(content);
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static string? Normalize(string content)
    {
        var text = content.AsSpan(0, Math.Min(content.Length, MaxCharacters)).TrimStart('\uFEFF').Trim();
        if (!text.IsEmpty && char.IsHighSurrogate(text[^1])) text = text[..^1];
        return text.IsEmpty ? null : text.ToString();
    }
}
