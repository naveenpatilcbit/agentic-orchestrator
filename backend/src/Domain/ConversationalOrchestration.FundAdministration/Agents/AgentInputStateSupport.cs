using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Operations;

namespace ConversationalOrchestration.FundAdministration.Agents;

internal static class AgentInputStateSupport
{
    public static Dictionary<string, string?> LoadValues(AgentOperation operation)
    {
        var payload = JsonContent.Deserialize<Dictionary<string, string?>>(operation.DataJson);
        return payload is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(payload, StringComparer.OrdinalIgnoreCase);
    }

    public static string SerializeValues(IDictionary<string, string?> payload)
    {
        var compact = payload
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        return JsonContent.Serialize(compact);
    }

    public static IReadOnlyCollection<string> GetMissingRequiredFieldNames(
        IReadOnlyCollection<AgentInputFieldDefinition> fieldDefinitions,
        IReadOnlyDictionary<string, string?> payload) =>
        fieldDefinitions
            .Where(static field => field.Required)
            .Where(field => !payload.TryGetValue(field.Name, out var value) || string.IsNullOrWhiteSpace(value))
            .Select(field => field.Name)
            .ToArray();

    public static string BuildPendingClarification(
        IReadOnlyCollection<AgentInputFieldDefinition> fieldDefinitions,
        IReadOnlyCollection<string> missingFieldNames,
        string actionDescription) =>
        $"I still need {FormatFieldLabels(fieldDefinitions, missingFieldNames)} to {actionDescription}.";

    public static string BuildAssistantClarificationMessage(
        IReadOnlyCollection<AgentInputFieldDefinition> fieldDefinitions,
        IReadOnlyCollection<string> missingFieldNames,
        string capabilityDescription) =>
        $"I can handle {capabilityDescription}, but I still need {FormatFieldLabels(fieldDefinitions, missingFieldNames)}.";

    public static IReadOnlyCollection<ConversationMessage> GetRelevantConversationHistory(
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation) =>
        conversationHistory
            .Where(message => message.OperationId == operation.Id || message.Id == userMessage.Id)
            .OrderByDescending(message => message.CreatedAtUtc)
            .Take(8)
            .OrderBy(message => message.CreatedAtUtc)
            .ToArray();

    public static string? GetValue(IReadOnlyDictionary<string, string?> payload, string key) =>
        payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    public static void MergeIfPresent(IDictionary<string, string?> payload, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            payload[key] = value.Trim();
        }
    }

    public static string Slugify(string value) =>
        value.Trim().ToLowerInvariant().Replace(' ', '-');

    private static string FormatFieldLabels(
        IReadOnlyCollection<AgentInputFieldDefinition> fieldDefinitions,
        IReadOnlyCollection<string> missingFieldNames)
    {
        var labels = missingFieldNames
            .Select(name => fieldDefinitions.FirstOrDefault(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Label ?? name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return labels.Length switch
        {
            0 => "a little more detail",
            1 => $"the {labels[0]}",
            2 => $"the {labels[0]} and {labels[1]}",
            _ => $"these details: {string.Join(", ", labels[..^1])}, and {labels[^1]}"
        };
    }
}
