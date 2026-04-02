using FundOrchestrator.Application.Abstractions;
using FundOrchestrator.Domain.Agents;
using FundOrchestrator.Domain.Files;
using FundOrchestrator.Domain.Operations;
using FundOrchestrator.Domain.Reviews;
using FundOrchestrator.Domain.Conversations;

namespace FundOrchestrator.Application.Operations;

public sealed class MessageRoutingService : IMessageRoutingService
{
    private readonly IAgentCatalog _agentCatalog;

    public MessageRoutingService(IAgentCatalog agentCatalog)
    {
        _agentCatalog = agentCatalog;
    }

    public Task<RoutingDecision> DecideAsync(
        string message,
        ConversationThread conversation,
        IReadOnlyCollection<AgentOperation> operations,
        IReadOnlyCollection<ReviewTask> reviewTasks,
        IReadOnlyCollection<FileAsset> attachments,
        CancellationToken cancellationToken)
    {
        var normalized = message.Trim().ToLowerInvariant();
        var activeOperations = operations
            .Where(static operation => operation.Status is not AgentOperationStatus.Completed and not AgentOperationStatus.Failed and not AgentOperationStatus.Cancelled)
            .OrderByDescending(operation => operation.UpdatedAtUtc)
            .ToArray();

        var referencedOperation = FindReferencedOperation(normalized, activeOperations);
        var referencedTask = FindReferencedTask(normalized, reviewTasks, operations);

        if (LooksLikeReviewDecision(normalized))
        {
            if (referencedTask is not null)
            {
                return Task.FromResult(new RoutingDecision(
                    RoutingDecisionType.RespondToReviewTask,
                    OperationId: referencedTask.OperationId,
                    ReviewTaskId: referencedTask.Id,
                    Explanation: "Matched an explicit review task reference."));
            }

            var openTasks = reviewTasks.Where(static task => task.Status == ReviewTaskStatus.Open).ToArray();
            if (openTasks.Length == 1)
            {
                return Task.FromResult(new RoutingDecision(
                    RoutingDecisionType.RespondToReviewTask,
                    OperationId: openTasks[0].OperationId,
                    ReviewTaskId: openTasks[0].Id,
                    Explanation: "Single open review task matched the approval message."));
            }

            if (openTasks.Length > 1)
            {
                return Task.FromResult(new RoutingDecision(
                    RoutingDecisionType.AmbiguousNeedClarification,
                    Explanation: "Multiple open review tasks exist, so approval text is ambiguous."));
            }
        }

        if (LooksLikeStatusQuery(normalized))
        {
            if (referencedOperation is not null)
            {
                return Task.FromResult(new RoutingDecision(
                    RoutingDecisionType.AskStatus,
                    OperationId: referencedOperation.Id,
                    Explanation: "Status request matched a specific active operation."));
            }

            return Task.FromResult(new RoutingDecision(
                RoutingDecisionType.AskStatus,
                Explanation: "Provide a status summary of active work in the conversation."));
        }

        var classifiedNewWork = _agentCatalog.TryClassifyNewWork(normalized, attachments.Count > 0);
        if (classifiedNewWork is not null)
        {
            if (referencedOperation is not null && referencedOperation.AgentId == classifiedNewWork.Id && !LooksLikeClearlyNewRequest(normalized))
            {
                return Task.FromResult(new RoutingDecision(
                    RoutingDecisionType.ContinueOperation,
                    AgentId: referencedOperation.AgentId,
                    OperationId: referencedOperation.Id,
                    Explanation: "Message appears to continue the same agent context."));
            }

            return Task.FromResult(new RoutingDecision(
                RoutingDecisionType.StartNewOperation,
                AgentId: classifiedNewWork.Id,
                Explanation: "Detected a new request for a supported agent."));
        }

        var clarificationCandidate = activeOperations.FirstOrDefault(static operation => operation.Status == AgentOperationStatus.ClarificationRequired);
        if (clarificationCandidate is not null)
        {
            return Task.FromResult(new RoutingDecision(
                RoutingDecisionType.ContinueOperation,
                AgentId: clarificationCandidate.AgentId,
                OperationId: clarificationCandidate.Id,
                Explanation: "Continuing an operation that is waiting for clarification."));
        }

        if (referencedOperation is not null)
        {
            return Task.FromResult(new RoutingDecision(
                RoutingDecisionType.ContinueOperation,
                AgentId: referencedOperation.AgentId,
                OperationId: referencedOperation.Id,
                Explanation: "Message explicitly referenced an existing operation."));
        }

        if (activeOperations.Length > 1)
        {
            return Task.FromResult(new RoutingDecision(
                RoutingDecisionType.AmbiguousNeedClarification,
                Explanation: "Multiple active operations exist and the new message is not clearly a new request."));
        }

        if (activeOperations.Length == 1)
        {
            return Task.FromResult(new RoutingDecision(
                RoutingDecisionType.ContinueOperation,
                AgentId: activeOperations[0].AgentId,
                OperationId: activeOperations[0].Id,
                Explanation: "Using the single active operation as the best continuation candidate."));
        }

        return Task.FromResult(new RoutingDecision(
            RoutingDecisionType.AmbiguousNeedClarification,
            Explanation: "Unable to confidently map the request to a supported agent."));
    }

    private static bool LooksLikeReviewDecision(string message) =>
        message.Contains("approve") || message.Contains("reject") || message.Contains("looks good") || message.Contains("ship it");

    private static bool LooksLikeStatusQuery(string message) =>
        message.Contains("status") || message.Contains("where are we") || message.Contains("progress");

    private static bool LooksLikeClearlyNewRequest(string message) =>
        message.StartsWith("create ") || message.StartsWith("generate ") || message.StartsWith("start ") || message.StartsWith("also ");

    private static AgentOperation? FindReferencedOperation(string message, IReadOnlyCollection<AgentOperation> operations)
    {
        foreach (var operation in operations)
        {
            if (!string.IsNullOrWhiteSpace(operation.Title) && message.Contains(operation.Title.ToLowerInvariant()))
            {
                return operation;
            }

            if (operation.AgentId switch
            {
                AgentIds.NoticeCreation when message.Contains("notice") || message.Contains("capital call") => true,
                AgentIds.OnePager when message.Contains("one pager") || message.Contains("presentation") => true,
                AgentIds.FundOnboarding when message.Contains("onboarding") || message.Contains("fund creation") => true,
                _ => false
            })
            {
                return operation;
            }
        }

        return null;
    }

    private static ReviewTask? FindReferencedTask(
        string message,
        IReadOnlyCollection<ReviewTask> reviewTasks,
        IReadOnlyCollection<AgentOperation> operations)
    {
        foreach (var reviewTask in reviewTasks.Where(static task => task.Status == ReviewTaskStatus.Open))
        {
            if (message.Contains(reviewTask.Title.ToLowerInvariant()) || message.Contains(reviewTask.TaskType.ToLowerInvariant()))
            {
                return reviewTask;
            }

            var operation = operations.FirstOrDefault(candidate => candidate.Id == reviewTask.OperationId);
            if (operation is not null && !string.IsNullOrWhiteSpace(operation.Title) && message.Contains(operation.Title.ToLowerInvariant()))
            {
                return reviewTask;
            }
        }

        return null;
    }
}
