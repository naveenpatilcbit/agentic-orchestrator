using ConversationalOrchestration.Contracts;
using ConversationalOrchestration.Domain.Agents;
using ConversationalOrchestration.Domain.Auditing;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using ConversationalOrchestration.Domain.Workflows;
using ConversationalOrchestration.Application.Support;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace ConversationalOrchestration.Application.Abstractions;

public interface IConversationRepository
{
    Task<ConversationThread?> GetAsync(string conversationId, string tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ConversationThread>> ListByTenantAsync(string tenantId, CancellationToken cancellationToken);
    Task UpsertAsync(ConversationThread conversation, CancellationToken cancellationToken);
}

public interface IConversationMessageRepository
{
    Task AddAsync(ConversationMessage message, CancellationToken cancellationToken);
    Task UpsertAsync(ConversationMessage message, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ConversationMessage>> ListByConversationAsync(string conversationId, string tenantId, CancellationToken cancellationToken);
}

public interface IConversationHistoryCompactionService
{
    Task RefreshAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ChatMessage>> GetReducedConversationHistoryAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<ChatMessage>> GetReducedOperationHistoryAsync(
        string tenantId,
        string conversationId,
        string operationId,
        CancellationToken cancellationToken);
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

public interface IWorkflowInstanceRepository
{
    Task<WorkflowInstance?> GetAsync(string workflowInstanceId, string tenantId, CancellationToken cancellationToken);
    Task<WorkflowInstance?> GetByOperationAsync(string operationId, string tenantId, CancellationToken cancellationToken);
    Task UpsertAsync(WorkflowInstance instance, CancellationToken cancellationToken);
}

public interface IWorkflowPendingRequestRepository
{
    Task<WorkflowPendingRequest?> GetAsync(string pendingRequestId, string tenantId, CancellationToken cancellationToken);
    Task<WorkflowPendingRequest?> GetByRequestIdAsync(string workflowInstanceId, string requestId, string tenantId, CancellationToken cancellationToken);
    Task<WorkflowPendingRequest?> GetLatestOpenByOperationAsync(string operationId, string tenantId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<WorkflowPendingRequest>> ListByWorkflowInstanceAsync(string workflowInstanceId, string tenantId, CancellationToken cancellationToken);
    Task UpsertAsync(WorkflowPendingRequest request, CancellationToken cancellationToken);
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
        FileAssetKind kind,
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
}

public interface IMessageIntentClassifier
{
    Task<RoutingDecision> ClassifyAsync(
        string message,
        ConversationThread conversation,
        IReadOnlyCollection<AgentOperation> operations,
        IReadOnlyCollection<ReviewTask> reviewTasks,
        IReadOnlyCollection<FileAsset> attachments,
        IReadOnlyCollection<AgentDefinition> availableAgents,
        CancellationToken cancellationToken);
}

public interface ILlmChatClientFactory
{
    IChatClient? TryGetChatClient(LlmProfile profile);
}

/// <summary>
/// Contract implemented by every agent capability in the platform.
/// The chat orchestrator chooses which method to invoke based on the routing decision for the incoming message.
/// </summary>
public interface IAgent
{
    /// <summary>
    /// Static metadata used by the catalog, router, and UI to identify the agent and its execution mode.
    /// </summary>
    AgentDefinition Definition { get; }

    /// <summary>
    /// Invoked when the router decides the incoming message should start a brand new operation for this agent.
    /// This is the first execution entry point after the orchestrator creates and persists a fresh <see cref="AgentOperation" />.
    /// Typical examples are a new notice draft request, a new onboarding submission, or a new one-pager request.
    /// <paramref name="conversationHistory" /> contains the recent operation-scoped messages available at the moment execution starts.
    /// </summary>
    Task<AgentExecutionResult> StartAsync(
        ConversationThread conversation,
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Invoked when the router attaches the incoming message to an existing operation for this agent.
    /// Use this path for follow-up messages such as clarification answers, "continue" instructions, or additional input
    /// that should advance work already in progress instead of creating a new operation.
    /// <paramref name="conversationHistory" /> contains the recent messages already associated with the same operation so the
    /// implementation can extract missing fields without treating the full chat thread as workflow state.
    /// </summary>
    Task<AgentExecutionResult> ContinueAsync(
        ConversationThread conversation,
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Invoked when the user asks for the status of an operation and the orchestrator needs an agent-specific status summary.
    /// The implementation should translate the stored operation state and any related review tasks into a concise,
    /// user-facing status message without mutating workflow state.
    /// </summary>
    Task<string> DescribeStatusAsync(
        AgentOperation operation,
        IReadOnlyCollection<ReviewTask> relatedTasks,
        CancellationToken cancellationToken);
}

public interface IChatOrchestratorService
{
    Task<ConversationSnapshotResponse> CreateConversationAsync(
        TenantExecutionContext context,
        CancellationToken cancellationToken);

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

public interface IReviewPayloadRevisionService
{
    Task<ReviewPayloadRevisionResult> ReviseAsync(
        ReviewTask reviewTask,
        string currentPayloadJson,
        string changeRequestText,
        CancellationToken cancellationToken);
}

public interface IReviewContinuationHandler
{
    bool CanHandle(AgentOperation operation, ReviewTask reviewTask);

    Task HandleApprovedAsync(
        ReviewContinuationContext context,
        CancellationToken cancellationToken);
}

public interface IWorkflowDefinition
{
    string Name { get; }
    Type StartInputType { get; }
    Workflow Build(WorkflowBuildContext buildContext);
    RequestPortDescriptor ResolveRequestPort(string portId);
    Task<ExternalResponse?> TryCreateAutomaticResponseAsync(
        RequestInfoEvent requestInfoEvent,
        CancellationToken cancellationToken);
}

public interface IWorkflowRegistry
{
    IWorkflowDefinition Resolve(string workflowName);
}

public interface IWorkflowRuntimeService
{
    Task<WorkflowRunResult> StartAsync<TInput>(
        WorkflowStartRequest<TInput> request,
        CancellationToken cancellationToken)
        where TInput : notnull;

    Task<WorkflowRunResult> ResumeAsync(
        WorkflowResumeRequest request,
        CancellationToken cancellationToken);

    Task<WorkflowRunResult> RetryAsync(
        WorkflowRetryRequest request,
        CancellationToken cancellationToken);
}

public sealed record ReviewContinuationContext(
    ConversationThread Conversation,
    AgentOperation Operation,
    ReviewTask ReviewTask,
    TenantExecutionContext RequestContext);

public sealed record RequestPortDescriptor(
    string PortId,
    Type RequestType,
    Type ResponseType,
    RequestPort Port);

public sealed record WorkflowStartRequest<TInput>(
    string TenantId,
    string ConversationId,
    string OperationId,
    string WorkflowName,
    TInput Input,
    string? WorkflowInstanceId = null)
    where TInput : notnull;

public sealed record WorkflowResumeRequest(
    string TenantId,
    string WorkflowInstanceId,
    string PendingRequestId,
    string ResponsePayloadJson,
    string? CheckpointId = null);

public sealed record WorkflowBuildContext(
    string TenantId,
    string ConversationId,
    string WorkflowInstanceId,
    string OperationId);

public sealed record ExternalInputResumePayload(
    string MessageText,
    string Role = "user");

public sealed record WorkflowRetryRequest(
    string TenantId,
    string WorkflowInstanceId,
    string? CheckpointId = null);

public sealed record WorkflowOutputMessage(
    string OutputType,
    object? Payload,
    string PayloadJson,
    string? SourceId = null)
{
    public bool TryGetPayload<TPayload>(out TPayload? payload)
    {
        if (Payload is TPayload typedPayload)
        {
            payload = typedPayload;
            return true;
        }

        payload = JsonContent.Deserialize<TPayload>(PayloadJson);
        return payload is not null;
    }
}

public sealed record WorkflowRunResult(
    WorkflowInstance Instance,
    IReadOnlyCollection<WorkflowPendingRequest> PendingRequests,
    IReadOnlyCollection<WorkflowOutputMessage> Outputs,
    IReadOnlyCollection<string> ActivatedExecutors,
    string? ErrorMessage = null);

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

public sealed record ReviewPayloadRevisionResult(
    bool Success,
    string? RevisedPayloadJson = null,
    string? ClarificationPrompt = null,
    string? Summary = null);
