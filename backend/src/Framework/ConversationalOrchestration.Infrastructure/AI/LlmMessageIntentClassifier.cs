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

public sealed class LlmConversationRoutingAgent : IConversationRoutingAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IMultiTurnStructuredAgentClient _multiTurnStructuredAgentClient;
    private readonly ILogger<LlmConversationRoutingAgent> _logger;

    public LlmConversationRoutingAgent(
        IMultiTurnStructuredAgentClient multiTurnStructuredAgentClient,
        ILogger<LlmConversationRoutingAgent> logger)
    {
        _multiTurnStructuredAgentClient = multiTurnStructuredAgentClient;
        _logger = logger;
    }

    public async Task<RoutingDecision> RouteAsync(
        string message,
        ConversationThread conversation,
        AgentOperation? activeOperation,
        ReviewTask? activeReviewTask,
        IReadOnlyCollection<CompletedOperationOutputSummary> completedOutputs,
        IReadOnlyCollection<FileAsset> attachments,
        IReadOnlyCollection<AgentDefinition> availableAgents,
        CancellationToken cancellationToken)
    {
        var decision = await _multiTurnStructuredAgentClient.GetAsync<LlmRoutingDecision>(
            new MultiTurnStructuredAgentRequest(
                LlmProfile.Routing,
                conversation.TenantId,
                conversation.Id,
                OperationId: null,
                BuildSystemPrompt(),
                BuildUserPrompt(
                    message,
                    conversation,
                    activeOperation,
                    activeReviewTask,
                    completedOutputs,
                    attachments,
                    availableAgents),
                AgentId: "routing-agent",
                AgentName: "Routing agent",
                AgentDescription: "Classifies user messages into orchestration routing decisions.",
                ChatOptions: null),
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
        - There can be at most one active operation and at most one open review task in the session.
        - If the user is approving or rejecting a review task, prefer RespondToReviewTask.
        - If the user asks for status, use AskStatus.
        - If an active operation exists and the user is answering its clarification, continuing its work, or referring to the current task, use ContinueOperation.
        - If an active operation exists and the user asks to start unrelated new work in the same thread, use AnswerDirectly and tell them to start a new thread or finish the current work first.
        - Completed outputs are reusable results from prior finished operations. Use them when the user asks to do the next step from prior approved work.
        - If the user is starting a fresh request for an available capability, inspect that agent's startRequirements and the available attachments before deciding whether the request is ready to start.
        - If the requested agent depends on a compatible completed output, inspect completedOutputs and the agent sourceRequirements.
        - When starting new work from a prior completed result, return StartNewOperation and set sourceOutputId when a specific completed output is clearly the best match.
        - Prefer a completed output from the last focused operation when the user says things like "this", "that", or "now do the next step".
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
          "sourceOutputId": "optional-completed-output-id for StartNewOperation",
          "explanation": "short explanation",
          "assistantMessage": "required only for AnswerDirectly"
        }
        """;

    private static string BuildUserPrompt(
        string message,
        ConversationThread conversation,
        AgentOperation? activeOperation,
        ReviewTask? activeReviewTask,
        IReadOnlyCollection<CompletedOperationOutputSummary> completedOutputs,
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
                conversation.LastFocusedOperationId,
                conversation.ActiveOperationId
            },
            activeOperation = activeOperation is null
                ? null
                : new
                {
                    activeOperation.Id,
                    activeOperation.AgentId,
                    activeOperation.Title,
                    Status = activeOperation.Status.ToString(),
                    activeOperation.CurrentStep,
                    activeOperation.PendingClarification,
                    activeOperation.UpdatedAtUtc
                },
            activeReviewTask = activeReviewTask is null
                ? null
                : new
                {
                    activeReviewTask.Id,
                    activeReviewTask.OperationId,
                    activeReviewTask.Title,
                    activeReviewTask.TaskType,
                    Status = activeReviewTask.Status.ToString(),
                    activeReviewTask.UpdatedAtUtc
                },
            attachments = attachments.Select(file => new
            {
                file.Id,
                file.FileName,
                file.ContentType
            }),
            completedOutputs = completedOutputs.Select(output => new
            {
                output.Id,
                output.OperationId,
                output.OperationTitle,
                output.SourceAgentId,
                output.OutputType,
                output.DisplayName,
                output.Summary,
                output.CompatibleAgentIds,
                output.IsLastFocusedOperation,
                output.UpdatedAtUtc
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
                    },
                SourceRequirements = agent.SourceRequirements is null
                    ? null
                    : new
                    {
                        agent.SourceRequirements.RequiresSource,
                        agent.SourceRequirements.Guidance,
                        agent.SourceRequirements.AcceptedOutputTypes
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

        [JsonPropertyName("sourceOutputId")]
        public string? SourceOutputId { get; init; }

        [JsonPropertyName("explanation")]
        public string? Explanation { get; init; }

        [JsonPropertyName("assistantMessage")]
        public string? AssistantMessage { get; init; }

        public RoutingDecision ToRoutingDecision() =>
            Enum.TryParse<RoutingDecisionType>(DecisionType, ignoreCase: true, out var routingDecisionType)
                ? new RoutingDecision(
                    routingDecisionType,
                    AgentId,
                    OperationId,
                    ReviewTaskId,
                    SourceOutputId: SourceOutputId,
                    Explanation: Explanation,
                    AssistantMessage: AssistantMessage)
                : Ambiguous($"Unsupported decision type '{DecisionType}'.");
    }
}
