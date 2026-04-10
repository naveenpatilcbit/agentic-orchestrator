namespace ConversationalOrchestration.Domain.Files;

public enum FileAssetKind
{
    UploadedInput = 1,
    GeneratedArtifact = 2
}

public sealed class FileAsset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public string RelativePath { get; set; } = string.Empty;
    public FileAssetKind Kind { get; set; } = FileAssetKind.UploadedInput;
    public long SizeBytes { get; set; }
    public DateTimeOffset UploadedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
