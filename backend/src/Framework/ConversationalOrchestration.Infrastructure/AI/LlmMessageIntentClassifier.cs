using System.Text.Json;
using System.Text.Json.Serialization;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Domain.Agents;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ConversationalOrchestration.Infrastructure.AI;

public sealed class LlmConversationRoutingAgent : IConversationRoutingAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IStructuredLlmClient _structuredLlmClient;
    private readonly ILogger<LlmConversationRoutingAgent> _logger;

    public LlmConversationRoutingAgent(
        IStructuredLlmClient structuredLlmClient,
        ILogger<LlmConversationRoutingAgent> logger)
    {
        _structuredLlmClient = structuredLlmClient;
        _logger = logger;
    }

    public async Task<RoutingDecision> RouteAsync(
        string message,
        ConversationThread conversation,
        IReadOnlyCollection<AgentOperation> operations,
        IReadOnlyCollection<ReviewTask> reviewTasks,
        IReadOnlyCollection<FileAsset> attachments,
        IReadOnlyCollection<ChatMessage> reducedConversationHistory,
        IReadOnlyCollection<AgentDefinition> availableAgents,
        CancellationToken cancellationToken)
    {
        var decision = await _structuredLlmClient.GetStructuredResponseAsync<LlmRoutingDecision>(
            new StructuredLlmRequest(
                LlmProfile.Routing,
                BuildSystemPrompt(),
                BuildUserPrompt(message, conversation, operations, reviewTasks, attachments, reducedConversationHistory, availableAgents)),
            cancellationToken);

        if (decision is null || string.IsNullOrWhiteSpace(decision.DecisionType))
        {
            _logger.LogWarning("Routing classifier returned no valid decision. Returning an ambiguous routing result.");
            return Ambiguous("Routing LLM returned no valid routing decision.");
        }

        return decision.ToRoutingDecision();
    }

    private static string BuildSystemPrompt() =>
        """
        You are the conversation routing agent for an orchestration platform.
        Your job is to decide whether to route the message into existing work, start new work, report status, or answer directly.

        Return JSON only. No markdown. No explanation outside JSON.

        Valid decisionType values:
        - ContinueOperation
        - RespondToReviewTask
        - AskStatus
        - StartNewOperation
        - AnswerDirectly
        - AmbiguousNeedClarification

        Rules:
        - Use only ids that exist in the provided context.
        - If the user is approving or rejecting a review task, prefer RespondToReviewTask.
        - If the user asks for status, use AskStatus.
        - If the user is starting a fresh request for an available capability, inspect that agent's startRequirements, the reducedConversationHistory, and the available attachments before deciding whether the request is ready to start.
        - If the user clearly wants an available capability but required startup fields are still missing, use AnswerDirectly and ask only for the missing required inputs instead of starting the operation yet.
        - If the chosen agent requires an attachment and the attachment is not present, use AnswerDirectly and ask the user to upload it instead of starting the operation.
        - Optional startup fields can be mentioned helpfully, but they should not block StartNewOperation.
        - Use StartNewOperation only when the required startup inputs appear to already be present in the provided context.
        - If the user is answering a clarification question or continuing work already in progress, use ContinueOperation.
        - If the user is asking a general informational or conversational question that can be answered without creating, resuming, or checking an operation, use AnswerDirectly.
        - For AnswerDirectly, provide a concise, helpful assistantMessage grounded in general product knowledge and the context provided.
        - If there are multiple plausible review tasks or operations and the message is ambiguous, use AmbiguousNeedClarification.
        - Never invent an agentId, operationId, or reviewTaskId.
        - If uncertain, choose AmbiguousNeedClarification.

        Output schema:
        {
          "decisionType": "ContinueOperation | RespondToReviewTask | AskStatus | StartNewOperation | AnswerDirectly | AmbiguousNeedClarification",
          "agentId": "optional-agent-id",
          "operationId": "optional-operation-id",
          "reviewTaskId": "optional-review-task-id",
          "explanation": "short explanation",
          "assistantMessage": "required only for AnswerDirectly"
        }
        """;

    private static string BuildUserPrompt(
        string message,
        ConversationThread conversation,
        IReadOnlyCollection<AgentOperation> operations,
        IReadOnlyCollection<ReviewTask> reviewTasks,
        IReadOnlyCollection<FileAsset> attachments,
        IReadOnlyCollection<ChatMessage> reducedConversationHistory,
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
            reducedConversationHistory = reducedConversationHistory.Select(item => new
            {
                Role = item.Role.Value,
                item.Text
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
                ExecutionMode = agent.ExecutionMode.ToString(),
                StartRequirements = agent.StartRequirements is null
                    ? null
                    : new
                    {
                        agent.StartRequirements.AllowsPartialStart,
                        agent.StartRequirements.RequiresAttachment,
                        agent.StartRequirements.Guidance,
                        Fields = agent.StartRequirements.Fields.Select(field => new
                        {
                            field.Name,
                            field.Label,
                            field.Description,
                            field.Required,
                            field.Example
                        })
                    }
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

        [JsonPropertyName("assistantMessage")]
        public string? AssistantMessage { get; init; }

        public RoutingDecision ToRoutingDecision() =>
            Enum.TryParse<RoutingDecisionType>(DecisionType, ignoreCase: true, out var routingDecisionType)
                ? new RoutingDecision(routingDecisionType, AgentId, OperationId, ReviewTaskId, Explanation, AssistantMessage)
                : Ambiguous($"Unsupported decision type '{DecisionType}'.");
    }
}
