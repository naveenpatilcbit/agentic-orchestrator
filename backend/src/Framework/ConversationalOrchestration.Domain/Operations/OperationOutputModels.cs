namespace ConversationalOrchestration.Domain.Operations;

public enum OperationOutputStatus
{
    Published = 1,
    Archived = 2
}

public sealed class OperationOutput
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string SourceAgentId { get; set; } = string.Empty;
    public string OutputType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public OperationOutputStatus Status { get; set; } = OperationOutputStatus.Published;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public static class StandardAgentInputNames
{
    public const string SourceOutputId = "sourceOutputId";
    public const string SourceOperationId = "sourceOperationId";
}
