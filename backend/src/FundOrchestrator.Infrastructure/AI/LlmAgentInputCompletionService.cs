using System.Text.Json;
using System.Text.Json.Serialization;
using FundOrchestrator.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace FundOrchestrator.Infrastructure.AI;

public sealed class LlmAgentInputCompletionService : IAgentInputCompletionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IStructuredLlmClient _structuredLlmClient;
    private readonly ILogger<LlmAgentInputCompletionService> _logger;

    public LlmAgentInputCompletionService(
        IStructuredLlmClient structuredLlmClient,
        ILogger<LlmAgentInputCompletionService> logger)
    {
        _structuredLlmClient = structuredLlmClient;
        _logger = logger;
    }

    public async Task<AgentInputCompletionResult> CompleteAsync(
        AgentInputCompletionRequest request,
        CancellationToken cancellationToken)
    {
        var extraction = await _structuredLlmClient.GetStructuredResponseAsync<LlmFieldExtractionResponse>(
            new StructuredLlmRequest(
                LlmProfile.InputCompletion,
                BuildSystemPrompt(),
                BuildUserPrompt(request)),
            cancellationToken);

        if (extraction is null)
        {
            _logger.LogWarning("Input completion LLM returned no structured payload for agent {AgentId}.", request.AgentId);
        }

        return BuildResult(request, extraction);
    }

    private static AgentInputCompletionResult BuildResult(
        AgentInputCompletionRequest request,
        LlmFieldExtractionResponse? extraction)
    {
        var allowedFields = request.Fields
            .Select(field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var merged = new Dictionary<string, string?>(request.CurrentValues, StringComparer.OrdinalIgnoreCase);
        var updatedFields = new List<string>();

        if (extraction?.Fields is not null)
        {
            foreach (var entry in extraction.Fields)
            {
                if (!allowedFields.Contains(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                {
                    continue;
                }

                var normalizedValue = entry.Value.Trim();
                if (!merged.TryGetValue(entry.Key, out var currentValue) ||
                    !string.Equals(currentValue, normalizedValue, StringComparison.Ordinal))
                {
                    merged[entry.Key] = normalizedValue;
                    updatedFields.Add(entry.Key);
                }
            }
        }

        var missingRequiredFields = request.Fields
            .Where(static field => field.Required)
            .Where(field => !merged.TryGetValue(field.Name, out var value) || string.IsNullOrWhiteSpace(value))
            .Select(field => field.Name)
            .ToArray();

        return new AgentInputCompletionResult(
            merged,
            missingRequiredFields,
            updatedFields,
            extraction?.Summary);
    }

    private static string BuildSystemPrompt() =>
        """
        You extract structured agent input fields for a fund administration orchestration platform.

        Return JSON only. No markdown. No explanation outside JSON.

        Rules:
        - Only fill fields that are explicitly present or strongly implied in the latest user message or the provided operation-scoped history.
        - Preserve existing values unless the latest user message clearly corrects or replaces them.
        - Never invent values.
        - If a field is not provided, omit it from the output instead of guessing.
        - Use concise normalized strings.
        - Ignore any fields not listed in the provided schema.

        Output schema:
        {
          "fields": {
            "fieldName": "value"
          },
          "summary": "short extraction summary"
        }
        """;

    private static string BuildUserPrompt(AgentInputCompletionRequest request)
    {
        var allowedFields = request.Fields
            .Select(field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var payload = new
        {
            request.AgentId,
            request.AgentDisplayName,
            request.CurrentStep,
            request.PendingClarification,
            request.LatestUserMessage,
            currentValues = request.CurrentValues
                .Where(entry => allowedFields.Contains(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value),
            fields = request.Fields.Select(field => new
            {
                field.Name,
                field.Label,
                field.Description,
                field.Required,
                field.Example
            }),
            relevantConversationHistory = request.RelevantConversationHistory
                .OrderBy(message => message.CreatedAtUtc)
                .TakeLast(8)
                .Select(message => new
                {
                    Role = message.Role.ToString(),
                    message.MessageKind,
                    message.Content,
                    message.CreatedAtUtc
                }),
            attachments = request.Attachments.Select(file => new
            {
                file.Id,
                file.FileName,
                file.ContentType
            })
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private sealed class LlmFieldExtractionResponse
    {
        [JsonPropertyName("fields")]
        public Dictionary<string, string>? Fields { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }
    }
}
