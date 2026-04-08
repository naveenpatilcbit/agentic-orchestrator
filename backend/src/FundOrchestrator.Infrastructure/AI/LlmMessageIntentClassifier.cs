using System.Text.Json;
using System.Text.Json.Serialization;
using FundOrchestrator.Application.Abstractions;
using FundOrchestrator.Domain.Agents;
using FundOrchestrator.Domain.Conversations;
using FundOrchestrator.Domain.Files;
using FundOrchestrator.Domain.Operations;
using FundOrchestrator.Domain.Reviews;
using Microsoft.Extensions.Logging;

namespace FundOrchestrator.Infrastructure.AI;

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
            _logger.LogWarning("Routing classifier returned no valid decision.");
            return Ambiguous("Routing LLM returned an invalid routing decision.");
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
