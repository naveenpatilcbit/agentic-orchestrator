using ConversationalOrchestration.Application.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace ConversationalOrchestration.Infrastructure.Conversations;

/// <summary>
/// Option A: Read-only chat history provider.
/// The application remains the single writer of the user-visible transcript (Mongo conversation_messages).
/// The Agent Framework calls into this provider only to fetch history for prompts.
/// </summary>
public sealed class MongoConversationChatHistoryProvider : ChatHistoryProvider
{
    private const string SessionStateKey = "MongoConversationChatHistoryProvider";

    private readonly IConversationHistoryCompactionService _conversationHistoryCompactionService;
    private readonly ProviderSessionState<MongoChatHistorySessionState> _sessionState;

    public MongoConversationChatHistoryProvider(
        IConversationHistoryCompactionService conversationHistoryCompactionService)
        : base(
            provideOutputMessageFilter: null,
            storeInputRequestMessageFilter: null,
            storeInputResponseMessageFilter: null)
    {
        _conversationHistoryCompactionService = conversationHistoryCompactionService;
        _sessionState = new ProviderSessionState<MongoChatHistorySessionState>(
            _ => new MongoChatHistorySessionState(),
            SessionStateKey);
    }

    public void BindSession(
        AgentSession session,
        string tenantId,
        string conversationId,
        string? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        var state = _sessionState.GetOrInitializeState(session);
        state.TenantId = tenantId;
        state.ConversationId = conversationId;
        state.OperationId = operationId;
        _sessionState.SaveState(session, state);
    }

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var state = GetBoundState(context.Session);
        if (state is null)
        {
            return Array.Empty<ChatMessage>();
        }

        return string.IsNullOrWhiteSpace(state.OperationId)
            ? await _conversationHistoryCompactionService.GetReducedConversationHistoryAsync(
                state.TenantId,
                state.ConversationId,
                cancellationToken)
            : await _conversationHistoryCompactionService.GetReducedOperationHistoryAsync(
                state.TenantId,
                state.ConversationId,
                state.OperationId,
                cancellationToken);
    }

    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default) =>
        // Read-only on purpose (Option A).
        ValueTask.CompletedTask;

    private MongoChatHistorySessionState? GetBoundState(AgentSession? session)
    {
        if (session is null)
        {
            return null;
        }

        var state = _sessionState.GetOrInitializeState(session);
        return string.IsNullOrWhiteSpace(state.TenantId) || string.IsNullOrWhiteSpace(state.ConversationId)
            ? null
            : state;
    }

    private sealed class MongoChatHistorySessionState
    {
        public string TenantId { get; set; } = string.Empty;
        public string ConversationId { get; set; } = string.Empty;
        public string? OperationId { get; set; }
    }
}

