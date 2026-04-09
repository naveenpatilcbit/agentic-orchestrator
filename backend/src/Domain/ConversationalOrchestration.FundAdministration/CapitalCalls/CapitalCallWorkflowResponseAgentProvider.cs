using System.Runtime.CompilerServices;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Domain.Conversations;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows.Declarative;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ConversationalOrchestration.FundAdministration.CapitalCalls;

public sealed class CapitalCallWorkflowResponseAgentProvider : ResponseAgentProvider
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public CapitalCallWorkflowResponseAgentProvider(
        IServiceScopeFactory serviceScopeFactory,
        CapitalCallWorkflowTools workflowTools)
    {
        _serviceScopeFactory = serviceScopeFactory;
        Functions = workflowTools.Functions;
    }

    public override Task<string> CreateConversationAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Guid.NewGuid().ToString("N"));

    public override async Task<ChatMessage> CreateMessageAsync(
        string conversationId,
        ChatMessage message,
        CancellationToken cancellationToken = default)
    {
        if (message.Role == ChatRole.User)
        {
            return message;
        }

        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var conversationRepository = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
        var messageRepository = scope.ServiceProvider.GetRequiredService<IConversationMessageRepository>();
        var conversation = await conversationRepository.GetAsync(conversationId, "tenant-demo", cancellationToken);
        if (conversation is null)
        {
            await conversationRepository.UpsertAsync(new ConversationThread
            {
                Id = conversationId,
                TenantId = "tenant-demo",
                Title = "Workflow conversation"
            }, cancellationToken);
        }

        await messageRepository.AddAsync(
            new ConversationMessage
            {
                TenantId = "tenant-demo",
                ConversationId = conversationId,
                AuthorId = "workflow",
                Role = message.Role == ChatRole.User
                    ? ConversationMessageRole.User
                    : message.Role == ChatRole.System
                        ? ConversationMessageRole.System
                        : ConversationMessageRole.Assistant,
                Content = message.Text,
                MessageKind = "workflow"
            },
            cancellationToken);

        var compactionService = scope.ServiceProvider.GetRequiredService<IConversationHistoryCompactionService>();
        await compactionService.RefreshAsync("tenant-demo", conversationId, cancellationToken);

        return message;
    }

    public override async Task<ChatMessage> GetMessageAsync(
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var messageRepository = scope.ServiceProvider.GetRequiredService<IConversationMessageRepository>();
        var messages = await messageRepository.ListByConversationAsync(conversationId, "tenant-demo", cancellationToken);
        var match = messages.FirstOrDefault(message => string.Equals(message.Id, messageId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new InvalidOperationException($"Conversation message '{messageId}' was not found.");
        }

        return MapMessage(match);
    }

    public override async IAsyncEnumerable<ChatMessage> GetMessagesAsync(
        string conversationId,
        int? limit = null,
        string? after = null,
        string? before = null,
        bool newestFirst = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var compactionService = scope.ServiceProvider.GetRequiredService<IConversationHistoryCompactionService>();
        var reducedMessages = await compactionService.GetReducedConversationHistoryAsync("tenant-demo", conversationId, cancellationToken);
        var ordered = newestFirst
            ? reducedMessages.Reverse()
            : reducedMessages;

        var mappedMessages = ordered
            .Take(limit ?? int.MaxValue)
            .ToArray();

        foreach (var message in mappedMessages)
        {
            yield return message;
        }
    }

    public override IAsyncEnumerable<AgentResponseUpdate> InvokeAgentAsync(
        string agentId,
        string agentVersion,
        string conversationId,
        IEnumerable<ChatMessage> messages,
        IDictionary<string, object> inputArguments,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This demo uses deterministic function tools inside the workflow and does not invoke named sub-agents.");

    private static ChatMessage MapMessage(ConversationMessage message) =>
        new(
            message.Role switch
            {
                ConversationMessageRole.Assistant => ChatRole.Assistant,
                ConversationMessageRole.System => ChatRole.System,
                _ => ChatRole.User
            },
            message.Content);
}
