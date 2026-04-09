using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Domain.Conversations;
using Microsoft.Extensions.AI;

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
    private static readonly Regex AmountRegex = new(
        @"(?<currency>₹|rs\.?|inr|\$|usd|eur)?\s*(?<amount>\d[\d,]*(?:\.\d{1,2})?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FundRegex = new(
        @"(?:for|fund)\s+(?<fund>[a-z0-9][a-z0-9\s&\-\._]+?)(?=\s+(?:for|amount|raising|raise|notice|capital)\b|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OverrideRegex = new(
        @"(?<partner>[a-z0-9][a-z0-9\s&\-\._]+?)\s+(?:override\s+)?(?:to|at)\s+(?<percentage>\d+(?:\.\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IConversationHistoryCompactionService _conversationHistoryCompactionService;
    private readonly IStructuredLlmClient _structuredLlmClient;

    public CapitalCallConversationIntelligence(
        IConversationHistoryCompactionService conversationHistoryCompactionService,
        IStructuredLlmClient structuredLlmClient)
    {
        _conversationHistoryCompactionService = conversationHistoryCompactionService;
        _structuredLlmClient = structuredLlmClient;
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
            cancellationToken);

    private async Task<CapitalCallIntentPatch> ExtractPatchAsync(
        string tenantId,
        string conversationId,
        string operationId,
        string latestMessage,
        string taskInstruction,
        CancellationToken cancellationToken)
    {
        var reducedMessages = await LoadCompactedMessagesAsync(tenantId, conversationId, operationId, cancellationToken);
        var effectiveLatestMessage = ResolveEffectiveLatestMessage(latestMessage, reducedMessages);
        var historyTranscript = BuildTranscript(reducedMessages);

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
            return llmPatch;
        }

        return BuildFallbackPatch(effectiveLatestMessage);
    }

    private async Task<IReadOnlyList<ChatMessage>> LoadCompactedMessagesAsync(
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

    private static CapitalCallIntentPatch BuildFallbackPatch(string latestMessage)
    {
        var patch = new CapitalCallIntentPatch();
        var normalized = latestMessage.Trim();

        var fundMatch = FundRegex.Match(normalized);
        if (fundMatch.Success)
        {
            patch.FundName = NormalizeName(fundMatch.Groups["fund"].Value);
        }

        var amountMatch = AmountRegex.Match(normalized);
        if (amountMatch.Success &&
            decimal.TryParse(
                amountMatch.Groups["amount"].Value.Replace(",", string.Empty, StringComparison.Ordinal),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            patch.CapitalCallAmount = amount;
        }

        foreach (Match match in OverrideRegex.Matches(normalized))
        {
            if (!decimal.TryParse(match.Groups["percentage"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var percentage))
            {
                continue;
            }

            patch.PartnerOverrides.Add(new CapitalCallPartnerOverride
            {
                PartnerName = NormalizeName(match.Groups["partner"].Value),
                CommitmentPercentage = percentage
            });
        }

        return patch;
    }

    private static string NormalizeName(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ");
}
