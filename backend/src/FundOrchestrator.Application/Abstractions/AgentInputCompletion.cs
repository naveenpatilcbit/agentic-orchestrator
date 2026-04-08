using FundOrchestrator.Domain.Conversations;
using FundOrchestrator.Domain.Files;

namespace FundOrchestrator.Application.Abstractions;

public sealed record AgentInputFieldDefinition(
    string Name,
    string Label,
    string Description,
    bool Required,
    string? Example = null);

public sealed record AgentInputCompletionRequest(
    string AgentId,
    string AgentDisplayName,
    string CurrentStep,
    string? PendingClarification,
    string LatestUserMessage,
    IReadOnlyCollection<ConversationMessage> RelevantConversationHistory,
    IReadOnlyCollection<FileAsset> Attachments,
    IReadOnlyDictionary<string, string?> CurrentValues,
    IReadOnlyCollection<AgentInputFieldDefinition> Fields);

public sealed record AgentInputCompletionResult(
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyCollection<string> MissingRequiredFields,
    IReadOnlyCollection<string> UpdatedFields,
    string? ExtractionSummary = null);

public interface IAgentInputCompletionService
{
    Task<AgentInputCompletionResult> CompleteAsync(
        AgentInputCompletionRequest request,
        CancellationToken cancellationToken);
}
