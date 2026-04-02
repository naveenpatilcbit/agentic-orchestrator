using FundOrchestrator.Contracts;
using FundOrchestrator.Contracts.Messaging;
using FundOrchestrator.Domain.Agents;
using FundOrchestrator.Domain.Auditing;
using FundOrchestrator.Domain.Conversations;
using FundOrchestrator.Domain.Files;
using FundOrchestrator.Domain.Operations;
using FundOrchestrator.Domain.Reviews;
using FundOrchestrator.Application.Support;

namespace FundOrchestrator.Application.Abstractions;

public interface IConversationRepository
{
    Task<ConversationThread?> GetAsync(string conversationId, string tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ConversationThread>> ListByTenantAsync(string tenantId, CancellationToken cancellationToken);
    Task UpsertAsync(ConversationThread conversation, CancellationToken cancellationToken);
}

public interface IConversationMessageRepository
{
    Task AddAsync(ConversationMessage message, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ConversationMessage>> ListByConversationAsync(string conversationId, string tenantId, CancellationToken cancellationToken);
}

public interface IAgentOperationRepository
{
    Task<AgentOperation?> GetAsync(string operationId, string tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<AgentOperation>> ListByConversationAsync(string conversationId, string tenantId, CancellationToken cancellationToken);
    Task UpsertAsync(AgentOperation operation, CancellationToken cancellationToken);
}

public interface IReviewTaskRepository
{
    Task<ReviewTask?> GetAsync(string reviewTaskId, string tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ReviewTask>> ListByConversationAsync(string conversationId, string tenantId, CancellationToken cancellationToken);
    Task UpsertAsync(ReviewTask task, CancellationToken cancellationToken);
}

public interface IAuditEventRepository
{
    Task AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}

public interface IFileAssetRepository
{
    Task<FileAsset?> GetAsync(string fileAssetId, string tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FileAsset>> ListByConversationAsync(string conversationId, string tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FileAsset>> ListByIdsAsync(IReadOnlyCollection<string> ids, string tenantId, CancellationToken cancellationToken);
    Task AddAsync(FileAsset fileAsset, CancellationToken cancellationToken);
}

public interface IFileStorageService
{
    Task<FileAsset> SaveAsync(
        Stream stream,
        string fileName,
        string contentType,
        string conversationId,
        TenantExecutionContext context,
        CancellationToken cancellationToken);
}

public interface IMessageRoutingService
{
    Task<RoutingDecision> DecideAsync(
        string message,
        ConversationThread conversation,
        IReadOnlyCollection<AgentOperation> operations,
        IReadOnlyCollection<ReviewTask> reviewTasks,
        IReadOnlyCollection<FileAsset> attachments,
        CancellationToken cancellationToken);
}

public interface IAgentCatalog
{
    IReadOnlyCollection<AgentDefinition> List();
    IAgent Resolve(string agentId);
    AgentDefinition? TryClassifyNewWork(string userMessage, bool hasAttachments);
}

public interface IAgent
{
    AgentDefinition Definition { get; }

    Task<AgentExecutionResult> StartAsync(
        ConversationThread conversation,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken);

    Task<AgentExecutionResult> ContinueAsync(
        ConversationThread conversation,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken);

    Task<string> DescribeStatusAsync(
        AgentOperation operation,
        IReadOnlyCollection<ReviewTask> relatedTasks,
        CancellationToken cancellationToken);
}

public interface IChatOrchestratorService
{
    Task<ConversationSnapshotResponse> HandleMessageAsync(
        ChatMessageRequest request,
        TenantExecutionContext context,
        CancellationToken cancellationToken);

    Task<ConversationSnapshotResponse?> GetSnapshotAsync(
        string conversationId,
        TenantExecutionContext context,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ConversationSummaryDto>> ListConversationsAsync(
        TenantExecutionContext context,
        CancellationToken cancellationToken);
}

public interface IReviewTaskService
{
    Task<ChatInteractionResult> SubmitAsync(
        string reviewTaskId,
        ReviewDecisionRequest request,
        TenantExecutionContext context,
        CancellationToken cancellationToken);

    Task<ChatInteractionResult> HandleChatDecisionAsync(
        RoutingDecision routingDecision,
        string message,
        TenantExecutionContext context,
        CancellationToken cancellationToken);
}

public interface IWorkflowCommandDispatcher
{
    Task StartOnboardingAsync(StartOnboardingWorkflowCommand command, CancellationToken cancellationToken);
    Task AdvanceOnboardingReviewAsync(AdvanceOnboardingReviewCommand command, CancellationToken cancellationToken);
}

public sealed record RoutingDecision(
    RoutingDecisionType Type,
    string? AgentId = null,
    string? OperationId = null,
    string? ReviewTaskId = null,
    string? Explanation = null);

public enum RoutingDecisionType
{
    ContinueOperation = 1,
    RespondToReviewTask = 2,
    AskStatus = 3,
    StartNewOperation = 4,
    AmbiguousNeedClarification = 5
}

public sealed record AgentExecutionResult(
    string AssistantMessage,
    AgentOperation Operation,
    IReadOnlyCollection<AgentAction>? Actions = null,
    IReadOnlyCollection<AuditEvent>? AuditEvents = null,
    string? FollowUpSystemMessage = null);

public sealed record ChatInteractionResult(
    string AssistantMessage,
    string ConversationId,
    string? OperationId = null);
