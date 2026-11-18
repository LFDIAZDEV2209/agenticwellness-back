namespace CoppAddresd.Application.Common.Storage;

/// <summary>Claves canónicas generadas por la subida ERP y el archivo clínico.</summary>
public static class PatientDocumentStorageKeys
{
    public static bool IsUploadKey(string key, Guid patientId)
    {
        var prefix = $"documents/{patientId:D}/";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var file = key.AsSpan(prefix.Length);
        var dot = file.IndexOf('.');
        if (dot < 0) return Guid.TryParseExact(file, "N", out _);
        return Guid.TryParseExact(file[..dot], "N", out _) && IsExtension(file[(dot + 1)..]);
    }

    public static bool IsArchiveOriginal(string key, Guid patientId, Guid documentId)
    {
        var prefix = $"documents/{patientId:D}/{documentId:D}/original.";
        return key.StartsWith(prefix, StringComparison.Ordinal) && IsExtension(key.AsSpan(prefix.Length));
    }

    private static bool IsExtension(ReadOnlySpan<char> extension)
    {
        if (extension.IsEmpty) return false;
        foreach (var character in extension)
        {
            if (!char.IsAsciiLetterOrDigit(character)) return false;
        }
        return true;
    }
}
