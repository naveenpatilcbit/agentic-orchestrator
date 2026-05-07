using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Conversations;

namespace ConversationalOrchestration.Application.Abstractions;

public interface IConversationPlanService
{
    Task<ConversationPlan?> TryCreateAsync(
        string messageText,
        ConversationThread conversation,
        TenantExecutionContext context,
        CancellationToken cancellationToken);

    Task<ConversationPlan?> GetActiveAsync(
        string conversationId,
        TenantExecutionContext context,
        CancellationToken cancellationToken);

    Task AdvanceAsync(
        string conversationId,
        TenantExecutionContext context,
        int nextStepIndex,
        CancellationToken cancellationToken);
}
