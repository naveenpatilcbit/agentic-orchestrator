#pragma warning disable MEAI001
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Infrastructure.AI;
using Microsoft.Extensions.AI;

namespace ConversationalOrchestration.Infrastructure.Conversations;

public sealed class ConversationHistoryCompactionService : IConversationHistoryCompactionService
{
    private const int ConversationTargetMessageCount = 12;
    private const int ConversationThresholdMessageCount = 16;
    private const int OperationTargetMessageCount = 8;
    private const int OperationThresholdMessageCount = 12;

    private readonly IConversationRepository _conversationRepository;
    private readonly IConversationMessageRepository _conversationMessageRepository;
    private readonly ILlmChatClientFactory _chatClientFactory;

    public ConversationHistoryCompactionService(
        IConversationRepository conversationRepository,
        IConversationMessageRepository conversationMessageRepository,
        ILlmChatClientFactory chatClientFactory)
    {
        _conversationRepository = conversationRepository;
        _conversationMessageRepository = conversationMessageRepository;
        _chatClientFactory = chatClientFactory;
    }

    public async Task RefreshAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversationRepository.GetAsync(conversationId, tenantId, cancellationToken);
        if (conversation is null)
        {
            return;
        }

        var storedMessages = await _conversationMessageRepository.ListByConversationAsync(conversationId, tenantId, cancellationToken);
        if (storedMessages.Count == 0)
        {
            conversation.ReducedHistoryJson = null;
            conversation.ReducedHistorySourceCount = 0;
            conversation.ReducedHistoryUpdatedAtUtc = DateTimeOffset.UtcNow;
            await _conversationRepository.UpsertAsync(conversation, cancellationToken);
            return;
        }

        // Persist the latest reduced transcript so other callers can reuse it without re-running
        // compaction every time they need conversation context for LLM prompts.
        var messages = storedMessages.Select(MapChatMessage).ToArray();
        var reducedMessages = await ReduceMessagesAsync(
            messages,
            ConversationTargetMessageCount,
            ConversationThresholdMessageCount,
            cancellationToken);

        conversation.ReducedHistoryJson = ShouldPersistReducedHistory(messages.Length, reducedMessages.Count)
            ? JsonContent.Serialize(reducedMessages.Select(MapStoredMessage).ToArray())
            : null;
        conversation.ReducedHistorySourceCount = messages.Length;
        conversation.ReducedHistoryUpdatedAtUtc = DateTimeOffset.UtcNow;

        await _conversationRepository.UpsertAsync(conversation, cancellationToken);
    }

    public async Task<IReadOnlyCollection<ReducedChatMessage>> GetReducedConversationHistoryAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversationRepository.GetAsync(conversationId, tenantId, cancellationToken);
        if (conversation is null)
        {
            return Array.Empty<ReducedChatMessage>();
        }

        var storedMessages = await _conversationMessageRepository.ListByConversationAsync(conversationId, tenantId, cancellationToken);
        if (storedMessages.Count == 0)
        {
            return Array.Empty<ReducedChatMessage>();
        }

        var messages = storedMessages.Select(MapChatMessage).ToArray();
        if (!string.IsNullOrWhiteSpace(conversation.ReducedHistoryJson) &&
            conversation.ReducedHistorySourceCount == messages.Length)
        {
            // The reduced snapshot is keyed by source message count, so it is safe to reuse until
            // a new raw message lands in the conversation.
            return LoadStoredMessages(conversation.ReducedHistoryJson);
        }

        var reducedMessages = await ReduceMessagesAsync(
            messages,
            ConversationTargetMessageCount,
            ConversationThresholdMessageCount,
            cancellationToken);

        conversation.ReducedHistoryJson = ShouldPersistReducedHistory(messages.Length, reducedMessages.Count)
            ? JsonContent.Serialize(reducedMessages.Select(MapStoredMessage).ToArray())
            : null;
        conversation.ReducedHistorySourceCount = messages.Length;
        conversation.ReducedHistoryUpdatedAtUtc = DateTimeOffset.UtcNow;
        await _conversationRepository.UpsertAsync(conversation, cancellationToken);

        return reducedMessages.Select(MapReducedChatMessage).ToArray();
    }

    public async Task<IReadOnlyCollection<ReducedChatMessage>> GetReducedOperationHistoryAsync(
        string tenantId,
        string conversationId,
        string operationId,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversationRepository.GetAsync(conversationId, tenantId, cancellationToken);
        if (conversation is null)
        {
            return Array.Empty<ReducedChatMessage>();
        }

        var storedMessages = await _conversationMessageRepository.ListHistoryAsync(conversationId, tenantId, operationId, cancellationToken);
        if (storedMessages.Count == 0)
        {
            return Array.Empty<ReducedChatMessage>();
        }

        var operationMessages = storedMessages.Select(MapChatMessage).ToArray();
        // Operation-scoped compaction keeps slot-filling and clarification prompts focused on one
        // unit of work even when the parent conversation contains multiple operations.
        var reduced = await ReduceMessagesAsync(
            operationMessages,
            OperationTargetMessageCount,
            OperationThresholdMessageCount,
            cancellationToken);
        return reduced.Select(MapReducedChatMessage).ToArray();
    }

    private async Task<IReadOnlyCollection<ChatMessage>> ReduceMessagesAsync(
        IReadOnlyCollection<ChatMessage> messages,
        int targetCount,
        int thresholdCount,
        CancellationToken cancellationToken)
    {
        var chatMessages = messages
            .ToArray();

        if (chatMessages.Length <= thresholdCount)
        {
            return chatMessages;
        }

        var chatClient = _chatClientFactory.TryGetChatClient(LlmProfile.InputCompletion);
        if (chatClient is not null)
        {
            // Prefer an LLM summary once the transcript grows large enough because it usually keeps
            // more intent-carrying context than a simple truncation strategy.
            var summarizingReducer = new SummarizingChatReducer(chatClient, targetCount, thresholdCount);
            var summarized = await summarizingReducer.ReduceAsync(chatMessages, cancellationToken);
            if (summarized is not null)
            {
                return summarized.ToArray();
            }
        }

        // Fall back to deterministic message counting when no summarizer is available or the
        // summarizer cannot produce a reduced transcript.
        var countingReducer = new MessageCountingChatReducer(targetCount);
        var counted = await countingReducer.ReduceAsync(chatMessages, cancellationToken);
        return (counted ?? chatMessages).ToArray();
    }

    private static bool ShouldPersistReducedHistory(int originalCount, int reducedCount) =>
        reducedCount > 0 && reducedCount < originalCount;

    private static IReadOnlyCollection<ReducedChatMessage> LoadStoredMessages(string reducedHistoryJson) =>
        (JsonContent.Deserialize<IReadOnlyCollection<StoredChatMessage>>(reducedHistoryJson) ?? Array.Empty<StoredChatMessage>())
        .Select(message => new ReducedChatMessage(ParseReducedRole(message.Role), message.Text))
        .ToArray();

    private static StoredChatMessage MapStoredMessage(ChatMessage message) =>
        new(message.Role.Value, message.Text);

    private static ReducedChatRole ParseReducedRole(string role) =>
        role switch
        {
            nameof(ChatRole.Assistant) or "assistant" => ReducedChatRole.Assistant,
            nameof(ChatRole.System) or "system" => ReducedChatRole.System,
            _ => ReducedChatRole.User
        };

    private static ChatMessage MapChatMessage(ConversationMessage message) =>
        new(
            message.Role switch
            {
                ConversationMessageRole.Assistant => ChatRole.Assistant,
                ConversationMessageRole.System => ChatRole.System,
                _ => ChatRole.User
            },
            message.Content);

    private static ReducedChatMessage MapReducedChatMessage(ChatMessage message) =>
        new(
            message.Role.Value switch
            {
                "assistant" => ReducedChatRole.Assistant,
                "system" => ReducedChatRole.System,
                _ => ReducedChatRole.User
            },
            message.Text);

    private sealed record StoredChatMessage(string Role, string Text);
}
