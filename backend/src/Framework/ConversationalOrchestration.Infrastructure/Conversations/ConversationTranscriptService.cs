using System.Security.Cryptography;
using System.Text;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Conversations;

namespace ConversationalOrchestration.Infrastructure.Conversations;

public sealed class ConversationTranscriptService : IConversationTranscriptService
{
    private readonly IConversationMessageRepository _conversationMessageRepository;
    private readonly IConversationHistoryCompactionService _conversationHistoryCompactionService;

    public ConversationTranscriptService(
        IConversationMessageRepository conversationMessageRepository,
        IConversationHistoryCompactionService conversationHistoryCompactionService)
    {
        _conversationMessageRepository = conversationMessageRepository;
        _conversationHistoryCompactionService = conversationHistoryCompactionService;
    }

    public async Task<ConversationMessage> AppendAsync(
        ConversationTranscriptAppendRequest request,
        CancellationToken cancellationToken)
    {
        var deduplicationKey = string.IsNullOrWhiteSpace(request.DeduplicationKey)
            ? Guid.NewGuid().ToString("N")
            : request.DeduplicationKey.Trim();

        var message = new ConversationMessage
        {
            Id = CreateStableMessageId(request.TenantId, request.ConversationId, deduplicationKey),
            TenantId = request.TenantId,
            ConversationId = request.ConversationId,
            OperationId = request.OperationId,
            AuthorId = request.AuthorId,
            Role = request.Role,
            Content = request.Content,
            MessageKind = request.MessageKind,
            ActionsJson = request.Actions is null ? null : JsonContent.Serialize(request.Actions),
            DeduplicationKey = deduplicationKey,
            SourceType = request.SourceType,
            SourceMessageId = request.SourceMessageId,
            MetadataJson = request.MetadataJson,
            CreatedAtUtc = request.CreatedAtUtc ?? DateTimeOffset.UtcNow
        };

        await _conversationMessageRepository.UpsertAsync(message, cancellationToken);
        await _conversationHistoryCompactionService.RefreshAsync(
            request.TenantId,
            request.ConversationId,
            cancellationToken);

        return message;
    }

    public Task<IReadOnlyCollection<ConversationMessage>> ListByConversationAsync(
        string conversationId,
        string tenantId,
        CancellationToken cancellationToken) =>
        _conversationMessageRepository.ListByConversationAsync(conversationId, tenantId, cancellationToken);

    public Task<IReadOnlyCollection<ConversationMessage>> ListHistoryAsync(
        string conversationId,
        string tenantId,
        string? operationId,
        CancellationToken cancellationToken) =>
        _conversationMessageRepository.ListHistoryAsync(conversationId, tenantId, operationId, cancellationToken);

    private static string CreateStableMessageId(
        string tenantId,
        string conversationId,
        string deduplicationKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}:{conversationId}:{deduplicationKey}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
