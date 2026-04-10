using System.Text.Json;
using System.Text.Json.Serialization;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Domain.Reviews;
using Microsoft.Extensions.Logging;

namespace ConversationalOrchestration.Infrastructure.AI;

public sealed class LlmReviewPayloadRevisionService : IReviewPayloadRevisionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly IStructuredLlmClient _structuredLlmClient;
    private readonly ILogger<LlmReviewPayloadRevisionService> _logger;

    public LlmReviewPayloadRevisionService(
        IStructuredLlmClient structuredLlmClient,
        ILogger<LlmReviewPayloadRevisionService> logger)
    {
        _structuredLlmClient = structuredLlmClient;
        _logger = logger;
    }

    public async Task<ReviewPayloadRevisionResult> ReviseAsync(
        ReviewTask reviewTask,
        string currentPayloadJson,
        string changeRequestText,
        CancellationToken cancellationToken)
    {
        var revision = await _structuredLlmClient.GetStructuredResponseAsync<LlmReviewPayloadRevisionResponse>(
            new StructuredLlmRequest(
                LlmProfile.ReviewRevision,
                BuildSystemPrompt(),
                BuildUserPrompt(reviewTask, currentPayloadJson, changeRequestText)),
            cancellationToken);

        if (revision is null)
        {
            _logger.LogWarning(
                "Review payload revision returned no structured result for task {ReviewTaskId} ({TaskType}).",
                reviewTask.Id,
                reviewTask.TaskType);
            return new ReviewPayloadRevisionResult(
                false,
                ClarificationPrompt: "I couldn't safely apply those changes yet. Rephrase the requested edits or use the advanced payload editor.");
        }

        if (string.Equals(revision.Status, "NeedsClarification", StringComparison.OrdinalIgnoreCase))
        {
            return new ReviewPayloadRevisionResult(
                false,
                ClarificationPrompt: string.IsNullOrWhiteSpace(revision.ClarificationQuestion)
                    ? "I need a bit more detail before I can apply those changes safely."
                    : revision.ClarificationQuestion,
                Summary: revision.Summary);
        }

        if (revision.RevisedPayload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            _logger.LogWarning(
                "Review payload revision produced no revised payload for task {ReviewTaskId} ({TaskType}).",
                reviewTask.Id,
                reviewTask.TaskType);
            return new ReviewPayloadRevisionResult(
                false,
                ClarificationPrompt: "I couldn't produce an updated payload from those instructions. Try being more specific or use the advanced payload editor.");
        }

        var revisedPayloadJson = revision.RevisedPayload.GetRawText();
        return new ReviewPayloadRevisionResult(
            true,
            RevisedPayloadJson: revisedPayloadJson,
            Summary: revision.Summary);
    }

    private static string BuildSystemPrompt() =>
        """
        You revise structured review-task payloads for a conversational workflow platform.

        Return JSON only. No markdown. No explanation outside JSON.

        Rules:
        - The existing payload is the source of truth. Apply only the user's requested changes.
        - Preserve all unchanged fields.
        - Keep the same overall JSON shape unless the user explicitly asks to add, remove, or move a value.
        - Never invent facts that are not already in the payload or explicitly requested by the user.
        - If the user's request is ambiguous or unsafe to apply, do not guess. Return NeedsClarification with a short question.

        Output schema:
        {
          "status": "Updated | NeedsClarification",
          "summary": "short summary",
          "clarificationQuestion": "required only when status is NeedsClarification",
          "revisedPayload": { }
        }
        """;

    private static string BuildUserPrompt(
        ReviewTask reviewTask,
        string currentPayloadJson,
        string changeRequestText)
    {
        var payload = new
        {
            reviewTask.Id,
            reviewTask.Title,
            reviewTask.TaskType,
            reviewTask.InteractionMode,
            reviewTask.InstructionText,
            currentPayload = JsonSerializer.Deserialize<JsonElement>(currentPayloadJson),
            requestedChanges = changeRequestText
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private sealed class LlmReviewPayloadRevisionResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("clarificationQuestion")]
        public string? ClarificationQuestion { get; init; }

        [JsonPropertyName("revisedPayload")]
        public JsonElement RevisedPayload { get; init; }
    }
}
