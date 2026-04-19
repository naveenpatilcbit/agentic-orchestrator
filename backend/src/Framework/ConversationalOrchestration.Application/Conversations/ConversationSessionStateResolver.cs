using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;

namespace ConversationalOrchestration.Application.Conversations;

internal static class ConversationSessionStateResolver
{
    public static AgentOperation? ResolveActiveOperation(
        ConversationThread conversation,
        IReadOnlyCollection<AgentOperation> operations)
    {
        if (!string.IsNullOrWhiteSpace(conversation.ActiveOperationId))
        {
            var trackedOperation = operations.FirstOrDefault(operation =>
                string.Equals(operation.Id, conversation.ActiveOperationId, StringComparison.Ordinal) &&
                IsActiveOperation(operation));
            if (trackedOperation is not null)
            {
                return trackedOperation;
            }
        }

        return operations
            .Where(IsActiveOperation)
            .OrderByDescending(operation => operation.UpdatedAtUtc)
            .FirstOrDefault();
    }

    public static ReviewTask? ResolveActiveReviewTask(
        AgentOperation? activeOperation,
        IReadOnlyCollection<ReviewTask> reviewTasks)
    {
        if (activeOperation is null)
        {
            return null;
        }

        return reviewTasks
            .Where(task =>
                string.Equals(task.OperationId, activeOperation.Id, StringComparison.Ordinal) &&
                task.Status == ReviewTaskStatus.Open)
            .OrderByDescending(task => task.UpdatedAtUtc)
            .FirstOrDefault();
    }

    public static bool SyncConversationPointers(
        ConversationThread conversation,
        AgentOperation? activeOperation)
    {
        var changed = false;
        var nextActiveOperationId = activeOperation?.Id;
        if (!string.Equals(conversation.ActiveOperationId, nextActiveOperationId, StringComparison.Ordinal))
        {
            conversation.ActiveOperationId = nextActiveOperationId;
            changed = true;
        }

        if (activeOperation is not null &&
            !string.Equals(conversation.LastFocusedOperationId, activeOperation.Id, StringComparison.Ordinal))
        {
            conversation.LastFocusedOperationId = activeOperation.Id;
            changed = true;
        }

        return changed;
    }

    public static bool IsActiveOperation(AgentOperation operation) =>
        operation.Status is not AgentOperationStatus.Completed and not AgentOperationStatus.Failed and not AgentOperationStatus.Cancelled;
}
