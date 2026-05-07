using System.Text.RegularExpressions;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Conversations;

namespace ConversationalOrchestration.Application.Conversations;

public sealed class ConversationPlanService : IConversationPlanService
{
    private static readonly Regex NumberedLine = new(@"^\s*(\d+)\s*[\)\.]\s*(.+)$", RegexOptions.Compiled);

    private readonly IConversationPlanRepository _planRepository;

    public ConversationPlanService(IConversationPlanRepository planRepository)
    {
        _planRepository = planRepository;
    }

    public Task<ConversationPlan?> GetActiveAsync(
        string conversationId,
        TenantExecutionContext context,
        CancellationToken cancellationToken) =>
        _planRepository.GetActiveAsync(conversationId, context.TenantId, cancellationToken);

    public async Task<ConversationPlan?> TryCreateAsync(
        string messageText,
        ConversationThread conversation,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var existing = await _planRepository.GetActiveAsync(conversation.Id, context.TenantId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var steps = ParseSteps(messageText);
        if (steps.Count < 2)
        {
            return null;
        }

        var plan = new ConversationPlan
        {
            TenantId = context.TenantId,
            ConversationId = conversation.Id,
            OriginalPrompt = messageText.Trim(),
            Steps = steps
                .Select((text, index) => new ConversationPlanStep
                {
                    Index = index,
                    Text = text,
                    Status = ConversationPlanStepStatus.Pending
                })
                .ToList(),
            NextStepIndex = 0,
            Status = ConversationPlanStatus.Active,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await _planRepository.UpsertAsync(plan, cancellationToken);
        return plan;
    }

    public async Task AdvanceAsync(
        string conversationId,
        TenantExecutionContext context,
        int nextStepIndex,
        CancellationToken cancellationToken)
    {
        var plan = await _planRepository.GetActiveAsync(conversationId, context.TenantId, cancellationToken);
        if (plan is null)
        {
            return;
        }

        plan.NextStepIndex = Math.Max(0, Math.Min(nextStepIndex, plan.Steps.Count));
        if (plan.NextStepIndex >= plan.Steps.Count)
        {
            plan.Status = ConversationPlanStatus.Completed;
        }

        plan.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _planRepository.UpsertAsync(plan, cancellationToken);
    }

    private static List<string> ParseSteps(string messageText)
    {
        var steps = new SortedDictionary<int, string>();

        foreach (var rawLine in messageText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var match = NumberedLine.Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups[1].Value, out var num))
            {
                continue;
            }

            var text = match.Groups[2].Value.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            // Convert 1-based numbering into 0-based step index.
            steps[num - 1] = text;
        }

        if (steps.Count == 0)
        {
            return [];
        }

        // Return a contiguous list starting at 0. If numbering is gapped, we stop at first gap.
        var result = new List<string>();
        for (var i = 0; i < steps.Count + 4; i++)
        {
            if (!steps.TryGetValue(i, out var step))
            {
                break;
            }

            result.Add(step);
        }

        return result;
    }
}

