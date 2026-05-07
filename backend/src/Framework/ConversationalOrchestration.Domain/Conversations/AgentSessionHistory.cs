namespace ConversationalOrchestration.Domain.Conversations;

public sealed class AgentSessionHistory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string? OperationId { get; set; }
    public string MessagesJson { get; set; } = "[]";
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

