namespace FundOrchestrator.Domain.Operations;

public enum AgentOperationStatus
{
    Received = 1,
    ClarificationRequired = 2,
    Queued = 3,
    Running = 4,
    WaitingForExternalSystem = 5,
    WaitingForHumanReview = 6,
    Completed = 7,
    Failed = 8,
    Cancelled = 9
}

public enum AgentActionType
{
    Reply = 1,
    AskForMoreInfo = 2,
    OpenPageWithPrefill = 3,
    CreateDraft = 4,
    StartAsyncOperation = 5,
    ShowStatus = 6,
    HighlightReviewTask = 7,
    DownloadArtifact = 8
}

public sealed class AgentAction
{
    public AgentActionType Type { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? Route { get; set; }
    public string? PayloadJson { get; set; }
}

public sealed class AgentOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public AgentOperationStatus Status { get; set; } = AgentOperationStatus.Received;
    public string CurrentStep { get; set; } = "Intake";
    public string Summary { get; set; } = string.Empty;
    public string CreatedByUserId { get; set; } = string.Empty;
    public string? PendingClarification { get; set; }
    public string? ActiveReviewTaskId { get; set; }
    public string? DataJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
