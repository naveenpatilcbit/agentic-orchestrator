using System.Text;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Domain.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ConversationalOrchestration.FundAdministration.CapitalCalls;

public interface ICapitalCallConversationIntelligence
{
    Task<CapitalCallIntentPatch> CaptureIntentAsync(
        string tenantId,
        string conversationId,
        string operationId,
        string latestMessage,
        CancellationToken cancellationToken);

    Task<CapitalCallIntentPatch> InterpretClarificationAsync(
        string tenantId,
        string conversationId,
        string operationId,
        string latestMessage,
        CancellationToken cancellationToken);
}

public sealed class CapitalCallConversationIntelligence : ICapitalCallConversationIntelligence
{
    private readonly IConversationHistoryCompactionService _conversationHistoryCompactionService;
    private readonly IStructuredLlmClient _structuredLlmClient;
    private readonly ILogger<CapitalCallConversationIntelligence> _logger;

    public CapitalCallConversationIntelligence(
        IConversationHistoryCompactionService conversationHistoryCompactionService,
        IStructuredLlmClient structuredLlmClient,
        ILogger<CapitalCallConversationIntelligence> logger)
    {
        _conversationHistoryCompactionService = conversationHistoryCompactionService;
        _structuredLlmClient = structuredLlmClient;
        _logger = logger;
    }

    public Task<CapitalCallIntentPatch> CaptureIntentAsync(
        string tenantId,
        string conversationId,
        string operationId,
        string latestMessage,
        CancellationToken cancellationToken) =>
        ExtractPatchAsync(
            tenantId,
            conversationId,
            operationId,
            latestMessage,
            "Capture the initial capital call request details from the conversation.",
            useConversationHistory: true,
            cancellationToken);

    public Task<CapitalCallIntentPatch> InterpretClarificationAsync(
        string tenantId,
        string conversationId,
        string operationId,
        string latestMessage,
        CancellationToken cancellationToken) =>
        ExtractPatchAsync(
            tenantId,
            conversationId,
            operationId,
            latestMessage,
            "Capture only the new clarification values or explicit overrides from the conversation.",
            useConversationHistory: false,
            cancellationToken);

    private async Task<CapitalCallIntentPatch> ExtractPatchAsync(
        string tenantId,
        string conversationId,
        string operationId,
        string latestMessage,
        string taskInstruction,
        bool useConversationHistory,
        CancellationToken cancellationToken)
    {
        var reducedMessages = useConversationHistory
            ? await LoadCompactedConversationMessagesAsync(tenantId, conversationId, cancellationToken)
            : await LoadCompactedOperationMessagesAsync(tenantId, conversationId, operationId, cancellationToken);
        var effectiveLatestMessage = ResolveEffectiveLatestMessage(latestMessage, reducedMessages);
        var historyTranscript = BuildTranscript(reducedMessages);
        _logger.LogInformation(
            "Extracting capital call intent patch for tenant {TenantId} conversation {ConversationId} operation {OperationId}. Task={TaskInstruction} ReducedMessages={ReducedMessageCount}",
            tenantId,
            conversationId,
            operationId,
            taskInstruction,
            reducedMessages.Count);

        var llmPatch = await _structuredLlmClient.GetStructuredResponseAsync<CapitalCallIntentPatch>(
            new StructuredLlmRequest(
                LlmProfile.InputCompletion,
                """
                You extract structured data for a capital call notice workflow.
                Return only JSON matching the schema.
                Rules:
                - Only capture values explicitly present in the conversation.
                - fundName should be the requested fund.
                - capitalCallAmount should be numeric without currency symbols.
                - noticeDate is optional and should stay null unless the user supplied it.
                - partnerOverrides should contain only explicit overrides like "North Star Feeder to 55%".
                - Never invent partner names, percentages, or dates.
                """,
                $"""
                Task:
                {taskInstruction}

                Recent operation conversation:
                {historyTranscript}

                Latest user message:
                {effectiveLatestMessage}
                """),
            cancellationToken);

        if (llmPatch is not null && HasAnyValue(llmPatch))
        {
            _logger.LogInformation(
                "Capital call intent extraction used LLM result for tenant {TenantId} conversation {ConversationId} operation {OperationId}. FundName={FundName} Amount={Amount} OverrideCount={OverrideCount}",
                tenantId,
                conversationId,
                operationId,
                llmPatch.FundName,
                llmPatch.CapitalCallAmount,
                llmPatch.PartnerOverrides.Count);
            return llmPatch;
        }

        _logger.LogWarning(
            "Capital call intent extraction returned no usable structured values for tenant {TenantId} conversation {ConversationId} operation {OperationId}.",
            tenantId,
            conversationId,
            operationId);
        return new CapitalCallIntentPatch();
    }

    private async Task<IReadOnlyList<ChatMessage>> LoadCompactedConversationMessagesAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var reduced = await _conversationHistoryCompactionService.GetReducedConversationHistoryAsync(
            tenantId,
            conversationId,
            cancellationToken);
        return reduced.ToArray();
    }

    private async Task<IReadOnlyList<ChatMessage>> LoadCompactedOperationMessagesAsync(
        string tenantId,
        string conversationId,
        string operationId,
        CancellationToken cancellationToken)
    {
        var reduced = await _conversationHistoryCompactionService.GetReducedOperationHistoryAsync(
            tenantId,
            conversationId,
            operationId,
            cancellationToken);

        if (reduced.Count > 0)
        {
            return reduced.ToArray();
        }

        // The very first startup handoff can happen before older intake turns are attached to the
        // new operation, so fall back to the reduced conversation transcript when needed.
        reduced = await _conversationHistoryCompactionService.GetReducedConversationHistoryAsync(
            tenantId,
            conversationId,
            cancellationToken);
        return reduced.ToArray();
    }

    private static string BuildTranscript(IEnumerable<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.Append(message.Role.ToString());
            builder.Append(": ");
            builder.AppendLine(message.Text);
        }

        return builder.Length == 0 ? "(no prior messages)" : builder.ToString().Trim();
    }

    private static string ResolveEffectiveLatestMessage(
        string latestMessage,
        IReadOnlyCollection<ChatMessage> reducedMessages)
    {
        if (!string.IsNullOrWhiteSpace(latestMessage))
        {
            return latestMessage.Trim();
        }

        var lastUserMessage = reducedMessages
            .Reverse()
            .FirstOrDefault(message => message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.Text))
            ?.Text;

        return string.IsNullOrWhiteSpace(lastUserMessage)
            ? string.Empty
            : lastUserMessage.Trim();
    }

    private static bool HasAnyValue(CapitalCallIntentPatch patch) =>
        !string.IsNullOrWhiteSpace(patch.FundName) ||
        patch.CapitalCallAmount.HasValue ||
        !string.IsNullOrWhiteSpace(patch.NoticeDate) ||
        patch.PartnerOverrides.Count > 0;
}
