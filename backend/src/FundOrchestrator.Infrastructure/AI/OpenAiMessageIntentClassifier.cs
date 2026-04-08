using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FundOrchestrator.Application.Abstractions;
using FundOrchestrator.Domain.Agents;
using FundOrchestrator.Domain.Conversations;
using FundOrchestrator.Domain.Files;
using FundOrchestrator.Domain.Operations;
using FundOrchestrator.Domain.Reviews;
using FundOrchestrator.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FundOrchestrator.Infrastructure.AI;

public sealed class OpenAiMessageIntentClassifier : IMessageIntentClassifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly IOptions<RoutingLlmOptions> _options;
    private readonly ILogger<OpenAiMessageIntentClassifier> _logger;

    public OpenAiMessageIntentClassifier(
        HttpClient httpClient,
        IOptions<RoutingLlmOptions> options,
        ILogger<OpenAiMessageIntentClassifier> logger)
    {
        _httpClient = httpClient;
        _options = options;
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
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.Model))
        {
            _logger.LogWarning("Routing LLM is not configured. Falling back to ambiguity response.");
            return Ambiguous("Routing LLM is not configured.");
        }

        try
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds));

            var request = new ChatCompletionsRequest(
                options.Model,
                [
                    new ChatMessage("system", BuildSystemPrompt()),
                    new ChatMessage("user", BuildUserPrompt(message, conversation, operations, reviewTasks, attachments, availableAgents))
                ],
                0,
                new ResponseFormat("json_object"));

            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post,
                BuildEndpoint(options))
            {
                Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Routing LLM call failed with {StatusCode}. Body: {Body}", response.StatusCode, responseBody);
                return Ambiguous("Routing LLM request failed.");
            }

            var completion = JsonSerializer.Deserialize<ChatCompletionsResponse>(responseBody, JsonOptions);
            var content = completion?.Choices
                .FirstOrDefault()?
                .Message?
                .GetText();

            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("Routing LLM returned an empty response body.");
                return Ambiguous("Routing LLM returned no routing decision.");
            }

            var decision = JsonSerializer.Deserialize<LlmRoutingDecision>(content, JsonOptions);
            if (decision is null || string.IsNullOrWhiteSpace(decision.DecisionType))
            {
                _logger.LogWarning("Routing LLM returned invalid JSON: {Content}", content);
                return Ambiguous("Routing LLM returned an invalid routing decision.");
            }

            return decision.ToRoutingDecision();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Routing LLM classification failed.");
            return Ambiguous("Routing LLM classification failed.");
        }
    }

    private static string BuildEndpoint(RoutingLlmOptions options)
    {
        var baseUrl = options.BaseUrl.TrimEnd('/');
        var endpointPath = options.EndpointPath.StartsWith('/') ? options.EndpointPath : $"/{options.EndpointPath}";
        return $"{baseUrl}{endpointPath}";
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

    private sealed record ChatCompletionsRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyCollection<ChatMessage> Messages,
        [property: JsonPropertyName("temperature")] int Temperature,
        [property: JsonPropertyName("response_format")] ResponseFormat ResponseFormat);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ResponseFormat([property: JsonPropertyName("type")] string Type);

    private sealed class ChatCompletionsResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice> Choices { get; init; } = [];
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")]
        public ResponseMessage? Message { get; init; }
    }

    private sealed class ResponseMessage
    {
        [JsonPropertyName("content")]
        public JsonElement Content { get; init; }

        public string? GetText()
        {
            if (Content.ValueKind == JsonValueKind.String)
            {
                return Content.GetString();
            }

            if (Content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var part in Content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (part.TryGetProperty("type", out var type) &&
                    type.GetString() == "text" &&
                    part.TryGetProperty("text", out var text))
                {
                    return text.GetString();
                }
            }

            return null;
        }
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
