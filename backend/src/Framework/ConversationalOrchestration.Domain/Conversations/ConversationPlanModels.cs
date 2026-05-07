namespace ConversationalOrchestration.Domain.Conversations;

public enum ConversationPlanStatus
{
    Active = 1,
    Completed = 2,
    Cancelled = 3
}

public enum ConversationPlanStepStatus
{
    Pending = 1,
    Started = 2,
    Completed = 3,
    Blocked = 4
}

public sealed class ConversationPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string OriginalPrompt { get; set; } = string.Empty;
    public List<ConversationPlanStep> Steps { get; set; } = [];
    public int NextStepIndex { get; set; }
    public ConversationPlanStatus Status { get; set; } = ConversationPlanStatus.Active;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ConversationPlanStep
{
    public int Index { get; set; }
    public string Text { get; set; } = string.Empty;
    public ConversationPlanStepStatus Status { get; set; } = ConversationPlanStepStatus.Pending;
    public string? StartedOperationId { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
}

