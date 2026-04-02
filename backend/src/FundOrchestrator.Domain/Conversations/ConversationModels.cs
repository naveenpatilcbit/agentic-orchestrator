namespace FundOrchestrator.Domain.Conversations;

public enum ConversationMessageRole
{
    User = 1,
    Assistant = 2,
    System = 3
}

public sealed class ConversationThread
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string Title { get; set; } = "New conversation";
    public string? LastFocusedOperationId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ConversationMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string? OperationId { get; set; }
    public string AuthorId { get; set; } = string.Empty;
    public ConversationMessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public string MessageKind { get; set; } = "chat";
    public string? ActionsJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
