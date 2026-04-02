namespace FundOrchestrator.Contracts;

public sealed record ChatMessageRequest(
    string? ConversationId,
    string Message,
    IReadOnlyCollection<string>? AttachmentIds);

public sealed record ConversationSnapshotResponse(
    string ConversationId,
    string Title,
    IReadOnlyCollection<ConversationMessageDto> Messages,
    IReadOnlyCollection<AgentOperationDto> Operations,
    IReadOnlyCollection<ReviewTaskDto> ReviewTasks,
    IReadOnlyCollection<FileAssetDto> Files);

public sealed record ConversationSummaryDto(
    string ConversationId,
    string Title,
    int MessageCount,
    int ActiveOperationCount,
    int OpenReviewCount,
    DateTimeOffset UpdatedAtUtc);

public sealed record ConversationMessageDto(
    string Id,
    string ConversationId,
    string? OperationId,
    string Role,
    string Content,
    string MessageKind,
    IReadOnlyCollection<AgentActionDto> Actions,
    DateTimeOffset CreatedAtUtc);

public sealed record AgentActionDto(
    string Type,
    string Label,
    string? Route,
    string? PayloadJson);

public sealed record AgentOperationDto(
    string Id,
    string AgentId,
    string Title,
    string Status,
    string CurrentStep,
    string Summary,
    string? PendingClarification,
    string? ActiveReviewTaskId,
    DateTimeOffset UpdatedAtUtc);

public sealed record ReviewTaskDto(
    string Id,
    string OperationId,
    string Title,
    string TaskType,
    string Status,
    string ProposedPayloadJson,
    string? FinalPayloadJson,
    string? Notes,
    DateTimeOffset UpdatedAtUtc);

public sealed record FileAssetDto(
    string Id,
    string ConversationId,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset UploadedAtUtc);

public sealed record ReviewDecisionRequest(
    string Decision,
    string? FinalPayloadJson,
    string? Notes);
