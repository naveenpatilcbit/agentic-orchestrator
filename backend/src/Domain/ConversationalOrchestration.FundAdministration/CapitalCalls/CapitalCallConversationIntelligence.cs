using ConversationalOrchestration.Application.Abstractions;
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
    private readonly IMultiTurnStructuredAgentClient _multiTurnStructuredAgentClient;
    private readonly ILogger<CapitalCallConversationIntelligence> _logger;

    public CapitalCallConversationIntelligence(
        IMultiTurnStructuredAgentClient multiTurnStructuredAgentClient,
        ILogger<CapitalCallConversationIntelligence> logger)
    {
        _multiTurnStructuredAgentClient = multiTurnStructuredAgentClient;
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
        var effectiveLatestMessage = latestMessage?.Trim() ?? string.Empty;
        _logger.LogInformation(
            "Extracting capital call intent patch for tenant {TenantId} conversation {ConversationId} operation {OperationId}. Task={TaskInstruction} HistoryScope={HistoryScope}",
            tenantId,
            conversationId,
            operationId,
            taskInstruction,
            useConversationHistory ? "conversation" : "operation");

        var llmPatch = await _multiTurnStructuredAgentClient.GetAsync<CapitalCallIntentPatch>(
            new MultiTurnStructuredAgentRequest(
                LlmProfile.InputCompletion,
                tenantId,
                conversationId,
                OperationId: useConversationHistory ? null : operationId,
                SystemPrompt:
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
                UserPrompt:
                $"""
                Task:
                {taskInstruction}

                Latest user message:
                {effectiveLatestMessage}
                """,
                AgentId: "capital-call-intent",
                AgentName: "Capital call intent extractor",
                AgentDescription: "Extracts capital call intent fields from the conversation."),
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

    private static bool HasAnyValue(CapitalCallIntentPatch patch) =>
        !string.IsNullOrWhiteSpace(patch.FundName) ||
        patch.CapitalCallAmount.HasValue ||
        !string.IsNullOrWhiteSpace(patch.NoticeDate) ||
        patch.PartnerOverrides.Count > 0;
}
