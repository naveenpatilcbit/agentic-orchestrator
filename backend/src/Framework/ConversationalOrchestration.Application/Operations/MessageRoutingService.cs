using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using ConversationalOrchestration.Domain.Conversations;

namespace ConversationalOrchestration.Application.Operations;

public sealed class MessageRoutingService : IMessageRoutingService
{
    private readonly IAgentCatalog _agentCatalog;
    private readonly IConversationHistoryCompactionService _conversationHistoryCompactionService;
    private readonly IConversationRoutingAgent _routingAgent;

    public MessageRoutingService(
        IAgentCatalog agentCatalog,
        IConversationHistoryCompactionService conversationHistoryCompactionService,
        IConversationRoutingAgent routingAgent)
    {
        _agentCatalog = agentCatalog;
        _conversationHistoryCompactionService = conversationHistoryCompactionService;
        _routingAgent = routingAgent;
    }

    public async Task<RoutingDecision> DecideAsync(
        string message,
        ConversationThread conversation,
        IReadOnlyCollection<AgentOperation> operations,
        IReadOnlyCollection<ReviewTask> reviewTasks,
        IReadOnlyCollection<FileAsset> attachments,
        CancellationToken cancellationToken)
    {
        var activeOperations = operations
            .Where(static operation => operation.Status is not AgentOperationStatus.Completed and not AgentOperationStatus.Failed and not AgentOperationStatus.Cancelled)
            .OrderByDescending(operation => operation.UpdatedAtUtc)
            .ToArray();
        var availableAgents = _agentCatalog.List();
        var reducedConversationHistory = await _conversationHistoryCompactionService.GetReducedConversationHistoryAsync(
            conversation.TenantId,
            conversation.Id,
            cancellationToken);

        var classifierDecision = await _routingAgent.RouteAsync(
            message,
            conversation,
            activeOperations,
            reviewTasks,
            attachments,
            reducedConversationHistory,
            availableAgents,
            cancellationToken);

        return NormalizeDecision(
            classifierDecision,
            activeOperations,
            reviewTasks,
            availableAgents.Select(agent => agent.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static RoutingDecision NormalizeDecision(
        RoutingDecision decision,
        IReadOnlyCollection<AgentOperation> activeOperations,
        IReadOnlyCollection<ReviewTask> reviewTasks,
        ISet<string> allowedAgentIds)
    {
        return decision.Type switch
        {
            RoutingDecisionType.ContinueOperation => NormalizeContinueOperation(decision, activeOperations),
            RoutingDecisionType.RespondToReviewTask => NormalizeReviewTask(decision, activeOperations, reviewTasks),
            RoutingDecisionType.AskStatus => NormalizeAskStatus(decision, activeOperations),
            RoutingDecisionType.StartNewOperation => NormalizeStartNewOperation(decision, allowedAgentIds),
            RoutingDecisionType.AnswerDirectly => NormalizeDirectAnswer(decision),
            _ => new RoutingDecision(RoutingDecisionType.AmbiguousNeedClarification, Explanation: decision.Explanation ?? "The request is ambiguous.")
        };
    }

    private static RoutingDecision NormalizeContinueOperation(
        RoutingDecision decision,
        IReadOnlyCollection<AgentOperation> activeOperations)
    {
        var operation = activeOperations.FirstOrDefault(candidate => candidate.Id == decision.OperationId);

        if (operation is null && !string.IsNullOrWhiteSpace(decision.AgentId))
        {
            operation = activeOperations.FirstOrDefault(candidate => candidate.AgentId == decision.AgentId);
        }

        return operation is null
            ? new RoutingDecision(RoutingDecisionType.AmbiguousNeedClarification, Explanation: decision.Explanation ?? "No active operation matched the continuation request.")
            : new RoutingDecision(
                RoutingDecisionType.ContinueOperation,
                operation.AgentId,
                operation.Id,
                Explanation: decision.Explanation ?? "LLM classified the message as continuing an active operation.");
    }

    private static RoutingDecision NormalizeReviewTask(
        RoutingDecision decision,
        IReadOnlyCollection<AgentOperation> activeOperations,
        IReadOnlyCollection<ReviewTask> reviewTasks)
    {
        var reviewTask = reviewTasks.FirstOrDefault(task => task.Id == decision.ReviewTaskId && task.Status == ReviewTaskStatus.Open);

        if (reviewTask is null && !string.IsNullOrWhiteSpace(decision.OperationId))
        {
            reviewTask = reviewTasks.FirstOrDefault(task => task.OperationId == decision.OperationId && task.Status == ReviewTaskStatus.Open);
        }

        if (reviewTask is null)
        {
            return new RoutingDecision(RoutingDecisionType.AmbiguousNeedClarification, Explanation: decision.Explanation ?? "No open review task matched the request.");
        }

        var operation = activeOperations.FirstOrDefault(candidate => candidate.Id == reviewTask.OperationId);

        return new RoutingDecision(
            RoutingDecisionType.RespondToReviewTask,
            operation?.AgentId,
            reviewTask.OperationId,
            reviewTask.Id,
            decision.Explanation ?? "LLM classified the message as a review task decision.");
    }

    private static RoutingDecision NormalizeAskStatus(
        RoutingDecision decision,
        IReadOnlyCollection<AgentOperation> activeOperations)
    {
        if (string.IsNullOrWhiteSpace(decision.OperationId))
        {
            return new RoutingDecision(RoutingDecisionType.AskStatus, Explanation: decision.Explanation ?? "LLM classified the message as a status query.");
        }

        var operation = activeOperations.FirstOrDefault(candidate => candidate.Id == decision.OperationId);

        return operation is null
            ? new RoutingDecision(RoutingDecisionType.AskStatus, Explanation: decision.Explanation ?? "LLM classified the message as a status query.")
            : new RoutingDecision(
                RoutingDecisionType.AskStatus,
                operation.AgentId,
                operation.Id,
                Explanation: decision.Explanation ?? "LLM classified the message as a specific status query.");
    }

    private static RoutingDecision NormalizeStartNewOperation(RoutingDecision decision, ISet<string> allowedAgentIds) =>
        string.IsNullOrWhiteSpace(decision.AgentId) || !allowedAgentIds.Contains(decision.AgentId)
            ? new RoutingDecision(RoutingDecisionType.AmbiguousNeedClarification, Explanation: decision.Explanation ?? "LLM did not provide a valid agent id for new work.")
            : new RoutingDecision(
                RoutingDecisionType.StartNewOperation,
                decision.AgentId,
                Explanation: decision.Explanation ?? "LLM classified the message as a new request.");

    private static RoutingDecision NormalizeDirectAnswer(RoutingDecision decision) =>
        string.IsNullOrWhiteSpace(decision.AssistantMessage)
            ? new RoutingDecision(RoutingDecisionType.AmbiguousNeedClarification, Explanation: decision.Explanation ?? "The routing agent did not provide a direct answer.")
            : new RoutingDecision(
                RoutingDecisionType.AnswerDirectly,
                Explanation: decision.Explanation ?? "The routing agent answered directly.",
                AssistantMessage: decision.AssistantMessage);
}
