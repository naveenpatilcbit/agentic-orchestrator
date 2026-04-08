namespace ConversationalOrchestration.Domain.Workflows;

public enum WorkflowInstanceStatus
{
    Created = 1,
    Running = 2,
    WaitingForHumanInput = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6
}

public enum WorkflowPendingRequestStatus
{
    Open = 1,
    Responded = 2,
    Cancelled = 3
}

public sealed class WorkflowInstance
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public WorkflowInstanceStatus Status { get; set; } = WorkflowInstanceStatus.Created;
    public string CurrentStep { get; set; } = "Created";
    public string? LatestCheckpointId { get; set; }
    public string? LastPendingRequestId { get; set; }
    public string? LastError { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WorkflowPendingRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string WorkflowInstanceId { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string PortId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string RequestType { get; set; } = string.Empty;
    public string RequestPayloadJson { get; set; } = string.Empty;
    public string? ResponsePayloadJson { get; set; }
    public WorkflowPendingRequestStatus Status { get; set; } = WorkflowPendingRequestStatus.Open;
    public string? ReviewTaskId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RespondedAtUtc { get; set; }
}

public sealed class WorkflowCheckpointDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = string.Empty;
    public string CheckpointId { get; set; } = string.Empty;
    public string? ParentCheckpointId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
