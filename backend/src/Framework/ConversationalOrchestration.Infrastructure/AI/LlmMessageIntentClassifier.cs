using System.Text.Json;
using System.Text.Json.Serialization;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Domain.Agents;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using Microsoft.Extensions.Logging;

namespace ConversationalOrchestration.Infrastructure.AI;

public sealed class LlmMessageIntentClassifier : IMessageIntentClassifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IStructuredLlmClient _structuredLlmClient;
    private readonly ILogger<LlmMessageIntentClassifier> _logger;

    public LlmMessageIntentClassifier(
        IStructuredLlmClient structuredLlmClient,
        ILogger<LlmMessageIntentClassifier> logger)
    {
        _structuredLlmClient = structuredLlmClient;
        _logger = logger;
    }

    public async Task<RoutingDecision> ClassifyAsync(
        string message,
        ConversationThread conversation,
        IReadOnlyCollection<AgentOperation> operations,
        IReadOnlyCollection<ReviewTask> reviewTasks,
        IReadOnlyCollection<FileAsset> attachments,
        IReadOnlyCollection<AgentDefinition> availableAgents,
        CancellationToken cancellationToken)
    {
        var decision = await _structuredLlmClient.GetStructuredResponseAsync<LlmRoutingDecision>(
            new StructuredLlmRequest(
                LlmProfile.Routing,
                BuildSystemPrompt(),
                BuildUserPrompt(message, conversation, operations, reviewTasks, attachments, availableAgents)),
            cancellationToken);

        if (decision is null || string.IsNullOrWhiteSpace(decision.DecisionType))
        {
            _logger.LogWarning("Routing classifier returned no valid decision. Falling back to deterministic rescue routing.");
            return BuildFallbackDecision(message, operations, reviewTasks, availableAgents);
        }

        return decision.ToRoutingDecision();
    }

    private static string BuildSystemPrompt() =>
        """
        You classify user chat messages into a routing decision for a fund administration orchestration platform.

        Return JSON only. No markdown. No explanation outside JSON.

        Valid decisionType values:
        - ContinueOperation
        - RespondToReviewTask
        - AskStatus
        - StartNewOperation
        - AmbiguousNeedClarification

        Rules:
        - Use only ids that exist in the provided context.
        - If the user is approving or rejecting a review task, prefer RespondToReviewTask.
        - If the user asks for status, use AskStatus.
        - If the user is starting a fresh request for an available capability, use StartNewOperation with an allowed agentId.
        - If the user is answering a clarification question or continuing work already in progress, use ContinueOperation.
        - If there are multiple plausible review tasks or operations and the message is ambiguous, use AmbiguousNeedClarification.
        - Never invent an agentId, operationId, or reviewTaskId.
        - If uncertain, choose AmbiguousNeedClarification.

        Output schema:
        {
          "decisionType": "ContinueOperation | RespondToReviewTask | AskStatus | StartNewOperation | AmbiguousNeedClarification",
          "agentId": "optional-agent-id",
          "operationId": "optional-operation-id",
          "reviewTaskId": "optional-review-task-id",
          "explanation": "short explanation"
        }
        """;

    private static string BuildUserPrompt(
        string message,
        ConversationThread conversation,
        IReadOnlyCollection<AgentOperation> operations,
        IReadOnlyCollection<ReviewTask> reviewTasks,
        IReadOnlyCollection<FileAsset> attachments,
        IReadOnlyCollection<AgentDefinition> availableAgents)
    {
        var payload = new
        {
            message,
            conversation = new
            {
                conversation.Id,
                conversation.Title,
                conversation.LastFocusedOperationId
            },
            attachments = attachments.Select(file => new
            {
                file.Id,
                file.FileName,
                file.ContentType
            }),
            operations = operations.Select(operation => new
            {
                operation.Id,
                operation.AgentId,
                operation.Title,
                Status = operation.Status.ToString(),
                operation.CurrentStep,
                operation.PendingClarification,
                operation.UpdatedAtUtc
            }),
            reviewTasks = reviewTasks.Select(task => new
            {
                task.Id,
                task.OperationId,
                task.Title,
                task.TaskType,
                Status = task.Status.ToString(),
                task.UpdatedAtUtc
            }),
            availableAgents = availableAgents.Select(agent => new
            {
                agent.Id,
                agent.DisplayName,
                agent.Description,
                ExecutionMode = agent.ExecutionMode.ToString()
            })
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static RoutingDecision Ambiguous(string explanation) =>
        new(RoutingDecisionType.AmbiguousNeedClarification, Explanation: explanation);

    private static RoutingDecision BuildFallbackDecision(
        string message,
        IReadOnlyCollection<AgentOperation> operations,
        IReadOnlyCollection<ReviewTask> reviewTasks,
        IReadOnlyCollection<AgentDefinition> availableAgents)
    {
        var normalized = message.Trim().ToLowerInvariant();
        var activeOperations = operations
            .Where(operation => operation.Status is not AgentOperationStatus.Completed and not AgentOperationStatus.Failed and not AgentOperationStatus.Cancelled)
            .ToArray();

        if ((normalized.Contains("approve", StringComparison.Ordinal) || normalized.Contains("reject", StringComparison.Ordinal)) &&
            reviewTasks.Count(task => task.Status == ReviewTaskStatus.Open) == 1)
        {
            var task = reviewTasks.First(task => task.Status == ReviewTaskStatus.Open);
            return new RoutingDecision(
                RoutingDecisionType.RespondToReviewTask,
                OperationId: task.OperationId,
                ReviewTaskId: task.Id,
                Explanation: "Fallback routed an approval/rejection response to the single open review task.");
        }

        if ((normalized.Contains("status", StringComparison.Ordinal) || normalized.StartsWith("what's", StringComparison.Ordinal) || normalized.StartsWith("whats", StringComparison.Ordinal)) &&
            activeOperations.Length <= 1)
        {
            return new RoutingDecision(
                RoutingDecisionType.AskStatus,
                OperationId: activeOperations.SingleOrDefault()?.Id,
                Explanation: "Fallback classified the message as a status request.");
        }

        if (activeOperations.Length == 1 &&
            activeOperations[0].Status == AgentOperationStatus.ClarificationRequired &&
            !normalized.Contains("status", StringComparison.Ordinal))
        {
            return new RoutingDecision(
                RoutingDecisionType.ContinueOperation,
                OperationId: activeOperations[0].Id,
                Explanation: "Fallback attached the message to the single operation waiting for clarification.");
        }

        var agentId = ResolveAgentId(normalized, availableAgents);
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            return new RoutingDecision(
                RoutingDecisionType.StartNewOperation,
                AgentId: agentId,
                Explanation: "Fallback matched the message to an available agent capability.");
        }

        return Ambiguous("Routing LLM returned an invalid routing decision and fallback routing could not determine a safe target.");
    }

    private static string? ResolveAgentId(string normalizedMessage, IReadOnlyCollection<AgentDefinition> availableAgents)
    {
        if (normalizedMessage.Contains("capital call", StringComparison.Ordinal) || normalizedMessage.Contains("notice", StringComparison.Ordinal))
        {
            return availableAgents.FirstOrDefault(agent =>
                agent.DisplayName.Contains("Notice", StringComparison.OrdinalIgnoreCase))?.Id;
        }

        if (normalizedMessage.Contains("onboarding", StringComparison.Ordinal) ||
            normalizedMessage.Contains("agreement", StringComparison.Ordinal) ||
            normalizedMessage.Contains("document", StringComparison.Ordinal))
        {
            return availableAgents.FirstOrDefault(agent =>
                agent.DisplayName.Contains("Onboarding", StringComparison.OrdinalIgnoreCase))?.Id;
        }

        if (normalizedMessage.Contains("one-pager", StringComparison.Ordinal) || normalizedMessage.Contains("one pager", StringComparison.Ordinal))
        {
            return availableAgents.FirstOrDefault(agent =>
                agent.DisplayName.Contains("One", StringComparison.OrdinalIgnoreCase))?.Id;
        }

        return null;
    }

    private sealed class LlmRoutingDecision
    {
        [JsonPropertyName("decisionType")]
        public string? DecisionType { get; init; }

        [JsonPropertyName("agentId")]
        public string? AgentId { get; init; }

        [JsonPropertyName("operationId")]
        public string? OperationId { get; init; }

        [JsonPropertyName("reviewTaskId")]
        public string? ReviewTaskId { get; init; }

        [JsonPropertyName("explanation")]
        public string? Explanation { get; init; }

        public RoutingDecision ToRoutingDecision() =>
            Enum.TryParse<RoutingDecisionType>(DecisionType, ignoreCase: true, out var routingDecisionType)
                ? new RoutingDecision(routingDecisionType, AgentId, OperationId, ReviewTaskId, Explanation)
                : Ambiguous($"Unsupported decision type '{DecisionType}'.");
    }
}
