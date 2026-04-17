using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Domain.Agents;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using ConversationalOrchestration.Domain.Conversations;

namespace ConversationalOrchestration.Application.Operations;

public sealed class MessageRoutingService : IMessageRoutingService
{
    private readonly IAgentCatalog _agentCatalog;
    private readonly IOperationOutputRepository _operationOutputRepository;
    private readonly IConversationHistoryCompactionService _conversationHistoryCompactionService;
    private readonly IConversationRoutingAgent _routingAgent;

    public MessageRoutingService(
        IAgentCatalog agentCatalog,
        IOperationOutputRepository operationOutputRepository,
        IConversationHistoryCompactionService conversationHistoryCompactionService,
        IConversationRoutingAgent routingAgent)
    {
        _agentCatalog = agentCatalog;
        _operationOutputRepository = operationOutputRepository;
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
        var availableAgentsById = availableAgents.ToDictionary(agent => agent.Id, StringComparer.OrdinalIgnoreCase);
        var completedOutputs = (await _operationOutputRepository.ListByConversationAsync(conversation.Id, conversation.TenantId, cancellationToken))
            .Take(12)
            .Select(output =>
            {
                var sourceOperation = operations.FirstOrDefault(operation => operation.Id == output.OperationId);
                var compatibleAgentIds = availableAgents
                    .Where(agent => agent.SourceRequirements?.AcceptedOutputTypes.Contains(output.OutputType, StringComparer.OrdinalIgnoreCase) == true)
                    .Select(agent => agent.Id)
                    .ToArray();

                return new CompletedOperationOutputSummary(
                    output.Id,
                    output.OperationId,
                    sourceOperation?.Title,
                    output.SourceAgentId,
                    output.OutputType,
                    output.DisplayName,
                    output.Summary,
                    compatibleAgentIds,
                    output.UpdatedAtUtc,
                    output.OperationId == conversation.LastFocusedOperationId);
            })
            .ToArray();
        var reducedConversationHistory = await _conversationHistoryCompactionService.GetReducedConversationHistoryAsync(
            conversation.TenantId,
            conversation.Id,
            cancellationToken);

        var classifierDecision = await _routingAgent.RouteAsync(
            message,
            conversation,
            activeOperations,
            completedOutputs,
            reviewTasks,
            attachments,
            reducedConversationHistory,
            availableAgents,
            cancellationToken);

        return NormalizeDecision(
            classifierDecision,
            conversation.LastFocusedOperationId,
            activeOperations,
            completedOutputs,
            reviewTasks,
            availableAgentsById);
    }

    private static RoutingDecision NormalizeDecision(
        RoutingDecision decision,
        string? lastFocusedOperationId,
        IReadOnlyCollection<AgentOperation> activeOperations,
        IReadOnlyCollection<CompletedOperationOutputSummary> completedOutputs,
        IReadOnlyCollection<ReviewTask> reviewTasks,
        IReadOnlyDictionary<string, AgentDefinition> availableAgentsById)
    {
        return decision.Type switch
        {
            RoutingDecisionType.ContinueOperation => NormalizeContinueOperation(decision, activeOperations),
            RoutingDecisionType.RespondToReviewTask => NormalizeReviewTask(decision, activeOperations, reviewTasks),
            RoutingDecisionType.AskStatus => NormalizeAskStatus(decision, activeOperations),
            RoutingDecisionType.StartNewOperation => NormalizeStartNewOperation(decision, lastFocusedOperationId, completedOutputs, availableAgentsById),
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
            Explanation: decision.Explanation ?? "LLM classified the message as a review task decision.");
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

    private static RoutingDecision NormalizeStartNewOperation(
        RoutingDecision decision,
        string? lastFocusedOperationId,
        IReadOnlyCollection<CompletedOperationOutputSummary> completedOutputs,
        IReadOnlyDictionary<string, AgentDefinition> availableAgentsById)
    {
        if (string.IsNullOrWhiteSpace(decision.AgentId) || !availableAgentsById.ContainsKey(decision.AgentId))
        {
            return new RoutingDecision(RoutingDecisionType.AmbiguousNeedClarification, Explanation: decision.Explanation ?? "LLM did not provide a valid agent id for new work.");
        }

        var agent = availableAgentsById[decision.AgentId];
        var sourceRequirements = agent.SourceRequirements;
        if (sourceRequirements is null || sourceRequirements.AcceptedOutputTypes.Count == 0)
        {
            return new RoutingDecision(
                RoutingDecisionType.StartNewOperation,
                decision.AgentId,
                Explanation: decision.Explanation ?? "LLM classified the message as a new request.");
        }

        var compatibleOutputs = completedOutputs
            .Where(output => output.CompatibleAgentIds.Contains(agent.Id, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        CompletedOperationOutputSummary? resolvedOutput = null;
        if (!string.IsNullOrWhiteSpace(decision.SourceOutputId))
        {
            resolvedOutput = compatibleOutputs.FirstOrDefault(output => output.Id == decision.SourceOutputId);
            if (resolvedOutput is null)
            {
                return new RoutingDecision(
                    RoutingDecisionType.AmbiguousNeedClarification,
                    Explanation: decision.Explanation ?? "The selected completed output is not available for that follow-up request.");
            }
        }
        else
        {
            resolvedOutput = TryResolveImplicitSourceOutput(lastFocusedOperationId, compatibleOutputs);
        }

        if (resolvedOutput is null)
        {
            if (sourceRequirements.RequiresSource && compatibleOutputs.Length == 0)
            {
                return new RoutingDecision(
                    RoutingDecisionType.AnswerDirectly,
                    Explanation: decision.Explanation ?? "The requested agent needs a compatible completed output before it can start.",
                    AssistantMessage: sourceRequirements.Guidance
                        ?? $"I can start {agent.DisplayName}, but I first need a compatible completed output in this conversation.");
            }

            if (sourceRequirements.RequiresSource)
            {
                return new RoutingDecision(
                    RoutingDecisionType.AmbiguousNeedClarification,
                    Explanation: decision.Explanation ?? "Multiple compatible completed outputs are available, so the source needs to be clarified.");
            }
        }

        return new RoutingDecision(
            RoutingDecisionType.StartNewOperation,
            decision.AgentId,
            SourceOperationId: resolvedOutput?.OperationId,
            SourceOutputId: resolvedOutput?.Id,
            Explanation: decision.Explanation ?? "LLM classified the message as a new request.");
    }

    private static CompletedOperationOutputSummary? TryResolveImplicitSourceOutput(
        string? lastFocusedOperationId,
        IReadOnlyCollection<CompletedOperationOutputSummary> compatibleOutputs)
    {
        if (!string.IsNullOrWhiteSpace(lastFocusedOperationId))
        {
            var lastFocusedMatches = compatibleOutputs
                .Where(output => output.OperationId == lastFocusedOperationId)
                .OrderByDescending(output => output.UpdatedAtUtc)
                .ToArray();

            if (lastFocusedMatches.Length == 1)
            {
                return lastFocusedMatches[0];
            }
        }

        return compatibleOutputs.Count == 1
            ? compatibleOutputs.First()
            : null;
    }

    private static RoutingDecision NormalizeDirectAnswer(RoutingDecision decision) =>
        string.IsNullOrWhiteSpace(decision.AssistantMessage)
            ? new RoutingDecision(RoutingDecisionType.AmbiguousNeedClarification, Explanation: decision.Explanation ?? "The routing agent did not provide a direct answer.")
            : new RoutingDecision(
                RoutingDecisionType.AnswerDirectly,
                Explanation: decision.Explanation ?? "The routing agent answered directly.",
                AssistantMessage: decision.AssistantMessage);
}
