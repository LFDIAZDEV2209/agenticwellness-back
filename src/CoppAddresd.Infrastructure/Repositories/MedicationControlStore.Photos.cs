using System.Security.Cryptography;
using System.Text;
using CoppAddresd.Application.Common.Storage;
using CoppAddresd.Application.Features.Medications;
using CoppAddresd.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed partial class MedicationControlStore
{
    internal sealed record PendingMedicationPhoto(string StorageKey, MedicationPhotoUpload Upload)
    {
        public bool WriteAttempted { get; set; }
    }

    private IObjectStorageService PhotoStorage => objectStorage
        ?? throw new InvalidOperationException("El almacenamiento de fotos no está configurado.");

    private static PendingMedicationPhoto? PreparePhoto(Guid patientId, MedicationPhotoTarget target,
        Guid requestId, MedicationPhotoUpload? photo)
    {
        if (photo is null) return null;
        // La misma operación reutiliza el objeto aun si una transacción se reintenta.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"medication-photo:{target}:{requestId:D}:{photo.Sha256}"));
        var photoId = new Guid(hash.AsSpan(0, 16));
        var extension = photo.ContentType == "image/png" ? "png" : "jpg";
        return new($"documents/{patientId:D}/{photoId:D}/original.medication.{extension}", photo);
    }

    private async Task StorePhotoAsync(PendingMedicationPhoto? photo, CancellationToken ct)
    {
        if (photo is null) return;
        photo.Upload.Content.Position = 0;
        photo.WriteAttempted = true;
        await PhotoStorage.PutDocumentArchiveObjectAsync(photo.StorageKey, photo.Upload.Content, photo.Upload.ContentType, ct);
    }

    private async Task<T> InPatientPhotoTransactionAsync<T>(Guid patientId, PendingMedicationPhoto? photo,
        Func<Task<T>> operation, CancellationToken ct)
    {
        try { return await InPatientTransactionAsync(patientId, operation, ct); }
        catch when (photo?.WriteAttempted == true)
        {
            // Tras el rollback, confirmar el resultado bajo el mismo lock antes de borrar.
            // Si el commit fue ambiguo o un retry concurrente lo salvó, el objeto queda intacto.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await InPatientTransactionAsync(patientId, async () =>
                {
                    if (!await db.MedicationTreatments.AnyAsync(t => t.PatientId == patientId && t.PhotoStorageKey == photo.StorageKey, cleanup.Token)
                        && !await db.MedicationIntakeEvents.AnyAsync(e => e.PatientId == patientId && e.PhotoStorageKey == photo.StorageKey, cleanup.Token))
                        await PhotoStorage.DeleteDocumentArchiveObjectAsync(photo.StorageKey, cleanup.Token);
                    return true;
                }, cleanup.Token);
            }
            catch (Exception error) { logger?.LogWarning(error, "No se pudo limpiar una foto de medicación sin confirmar."); }
            throw;
        }
    }

    public async Task<MedicationPhotoContent> ReadPhotoAsync(Guid patientId, Guid id,
        MedicationPhotoTarget target, CancellationToken ct)
    {
        var key = target == MedicationPhotoTarget.Treatment
            ? await db.MedicationTreatments.Where(t => t.PatientId == patientId && t.Id == id)
                .Select(t => t.PhotoStorageKey).SingleOrDefaultAsync(ct)
            : await db.MedicationIntakeEvents.Where(e => e.PatientId == patientId && e.Id == id)
                .Select(e => e.PhotoStorageKey).SingleOrDefaultAsync(ct);
        if (key is null || !key.StartsWith($"documents/{patientId:D}/", StringComparison.Ordinal)) throw Missing();
        DocumentArchiveStorageKeys.RequireArchiveKey(key);
        var contentType = Path.GetExtension(key) switch
        {
            ".jpg" => "image/jpeg",
            ".png" => "image/png",
            _ => throw Missing(),
        };
        try { return new(await PhotoStorage.GetDocumentArchiveObjectAsync(key, ct), contentType); }
        catch (FileNotFoundException) { throw Missing(); }
    }
}
