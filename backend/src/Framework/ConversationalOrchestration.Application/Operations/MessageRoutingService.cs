using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Conversations;
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
    private readonly IConversationRoutingAgent _routingAgent;

    public MessageRoutingService(
        IAgentCatalog agentCatalog,
        IOperationOutputRepository operationOutputRepository,
        IConversationRoutingAgent routingAgent)
    {
        _agentCatalog = agentCatalog;
        _operationOutputRepository = operationOutputRepository;
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
        var activeOperation = ConversationSessionStateResolver.ResolveActiveOperation(conversation, operations);
        var activeReviewTask = ConversationSessionStateResolver.ResolveActiveReviewTask(activeOperation, reviewTasks);
        var availableAgents = _agentCatalog.List();
        var availableAgentsById = availableAgents.ToDictionary(agent => agent.Id, StringComparer.OrdinalIgnoreCase);
        // Exists so the router can support chaining off finished results without restating everything.
        var completedOutputs = await GetCompletedOperationOutputSummaries(conversation, operations, cancellationToken, availableAgents);

        var classifierDecision = await _routingAgent.RouteAsync(
            message,
            conversation,
            activeOperation,
            activeReviewTask,
            completedOutputs,
            attachments,
            availableAgents,
            cancellationToken);

        return NormalizeDecision(
            classifierDecision,
            conversation.LastFocusedOperationId,
            activeOperation,
            activeReviewTask,
            completedOutputs,
            availableAgentsById);
    }

    private async Task<CompletedOperationOutputSummary[]> GetCompletedOperationOutputSummaries(ConversationThread conversation, IReadOnlyCollection<AgentOperation> operations, CancellationToken cancellationToken, IReadOnlyCollection<AgentDefinition> availableAgents)
    {
        return (await _operationOutputRepository.ListByConversationAsync(conversation.Id, conversation.TenantId, cancellationToken))
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
    }

    private static RoutingDecision NormalizeDecision(
        RoutingDecision decision,
        string? lastFocusedOperationId,
        AgentOperation? activeOperation,
        ReviewTask? activeReviewTask,
        IReadOnlyCollection<CompletedOperationOutputSummary> completedOutputs,
        IReadOnlyDictionary<string, AgentDefinition> availableAgentsById)
    {
        return decision.Type switch
        {
            RoutingDecisionType.ContinueOperation => NormalizeContinueOperation(decision, activeOperation),
            RoutingDecisionType.RespondToReviewTask => NormalizeReviewTask(decision, activeOperation, activeReviewTask),
            RoutingDecisionType.AskStatus => NormalizeAskStatus(decision, activeOperation),
            RoutingDecisionType.StartNewOperation => NormalizeStartNewOperation(decision, activeOperation, lastFocusedOperationId, completedOutputs, availableAgentsById),
            RoutingDecisionType.AnswerDirectly => NormalizeDirectAnswer(decision),
            _ => new RoutingDecision(RoutingDecisionType.AmbiguousNeedClarification, Explanation: decision.Explanation ?? "The request is ambiguous.")
        };
    }

    private static RoutingDecision NormalizeContinueOperation(
        RoutingDecision decision,
        AgentOperation? activeOperation)
    {
        return activeOperation is null
            ? new RoutingDecision(
                RoutingDecisionType.AnswerDirectly,
                Explanation: decision.Explanation ?? "There is no active operation to continue in this thread.",
                AssistantMessage: "There is no active workflow in this thread right now. Start a new request, or open another thread if you want to resume different work.")
            : new RoutingDecision(
                RoutingDecisionType.ContinueOperation,
                activeOperation.AgentId,
                activeOperation.Id,
                Explanation: decision.Explanation ?? "LLM classified the message as continuing the active session operation.");
    }

    private static RoutingDecision NormalizeReviewTask(
        RoutingDecision decision,
        AgentOperation? activeOperation,
        ReviewTask? activeReviewTask)
    {
        if (activeReviewTask is null)
        {
            return new RoutingDecision(
                RoutingDecisionType.AnswerDirectly,
                Explanation: decision.Explanation ?? "There is no open review task in the active session.",
                AssistantMessage: "There is no open review task in this thread right now. Ask for status if you want a quick summary of the current workflow.");
        }

        return new RoutingDecision(
            RoutingDecisionType.RespondToReviewTask,
            activeOperation?.AgentId,
            activeReviewTask.OperationId,
            activeReviewTask.Id,
            Explanation: decision.Explanation ?? "LLM classified the message as a decision for the active review task.");
    }

    private static RoutingDecision NormalizeAskStatus(
        RoutingDecision decision,
        AgentOperation? activeOperation)
    {
        if (activeOperation is null)
        {
            return new RoutingDecision(RoutingDecisionType.AskStatus, Explanation: decision.Explanation ?? "LLM classified the message as a status query.");
        }

        return new RoutingDecision(
            RoutingDecisionType.AskStatus,
            activeOperation.AgentId,
            activeOperation.Id,
            Explanation: decision.Explanation ?? "LLM classified the message as a status query for the active session operation.");
    }

    private static RoutingDecision NormalizeStartNewOperation(
        RoutingDecision decision,
        AgentOperation? activeOperation,
        string? lastFocusedOperationId,
        IReadOnlyCollection<CompletedOperationOutputSummary> completedOutputs,
        IReadOnlyDictionary<string, AgentDefinition> availableAgentsById)
    {
        if (activeOperation is not null)
        {
            return new RoutingDecision(
                RoutingDecisionType.AnswerDirectly,
                Explanation: decision.Explanation ?? "A new operation cannot start while this thread already has active work.",
                AssistantMessage: $"This thread already has active work: {activeOperation.Title}. Continue it here, review its current checkpoint, or start a new thread for a separate request.");
        }

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
