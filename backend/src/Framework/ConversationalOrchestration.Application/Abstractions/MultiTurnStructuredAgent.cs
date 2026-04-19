namespace ConversationalOrchestration.Application.Abstractions;

/// <summary>
/// Framework-managed multi-turn structured output (Option A).
/// The application is still the source-of-truth writer for the transcript, but the Agent Framework
/// is responsible for loading the relevant chat history for a run via a <see cref="Microsoft.Agents.AI.ChatHistoryProvider"/>.
/// </summary>
public interface IMultiTurnStructuredAgentClient
{
    Task<TResponse?> GetAsync<TResponse>(
        MultiTurnStructuredAgentRequest request,
        CancellationToken cancellationToken)
        where TResponse : class;
}

public sealed record MultiTurnStructuredAgentRequest(
    LlmProfile Profile,
    string TenantId,
    string ConversationId,
    string? OperationId,
    string SystemPrompt,
    string UserPrompt,
    string AgentId,
    string AgentName,
    string? AgentDescription = null);
