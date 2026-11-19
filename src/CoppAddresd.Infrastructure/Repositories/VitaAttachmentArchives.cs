using CoppAddresd.Application.Common.Storage;
using CoppAddresd.Application.Features.Agents.Vita;
using CoppAddresd.Domain.Entities;
using CoppAddresd.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoppAddresd.Infrastructure.Repositories;

public sealed class VitaAttachmentArchives(AppDbContext db) : IVitaAttachmentArchives
{
    public async Task<Document?> FindAsync(Guid patientId, Guid attachmentId, CancellationToken ct)
    {
        var document = await db.Documents.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == attachmentId && x.PatientId == patientId && x.ParentDocumentId == null, ct);
        return document is not null &&
            PatientDocumentStorageKeys.TryParseVitaAttachment(document.StorageKey, out var owner, out var id)
            && owner == patientId && id == attachmentId ? document : null;
    }

    public async Task<bool> TryInsertAsync(Document document, CancellationToken ct)
    {
        db.Documents.Add(document);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, SchemaName: "app", TableName: "documents", ConstraintName: "PK_documents1" })
        {
            db.Entry(document).State = EntityState.Detached;
            return false;
        }
    }
}
