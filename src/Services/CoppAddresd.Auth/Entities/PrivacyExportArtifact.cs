namespace CoppAddresd.Auth.Entities;

public sealed class PrivacyExportArtifact
{
    public Guid RequestId { get; set; }
    public Guid PublishedBy { get; set; }
    public DateTime PublishedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public byte[]? ProtectedContent { get; set; }
}
