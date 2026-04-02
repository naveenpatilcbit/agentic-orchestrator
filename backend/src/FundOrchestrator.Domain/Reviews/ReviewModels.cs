namespace FundOrchestrator.Domain.Reviews;

public enum ReviewTaskStatus
{
    Open = 1,
    Approved = 2,
    Rejected = 3,
    NeedsChanges = 4
}

public sealed class ReviewTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string TaskType { get; set; } = string.Empty;
    public string ProposedPayloadJson { get; set; } = string.Empty;
    public string? FinalPayloadJson { get; set; }
    public string? Notes { get; set; }
    public ReviewTaskStatus Status { get; set; } = ReviewTaskStatus.Open;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
