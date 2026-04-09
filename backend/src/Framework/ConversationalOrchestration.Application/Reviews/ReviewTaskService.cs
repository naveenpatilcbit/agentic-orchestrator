using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Contracts;
using ConversationalOrchestration.Domain.Auditing;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using System.Text.Json;

namespace ConversationalOrchestration.Application.Reviews;

public sealed class ReviewTaskService : IReviewTaskService
{
    private readonly IReviewTaskRepository _reviewTaskRepository;
    private readonly IAgentOperationRepository _operationRepository;
    private readonly IConversationRepository _conversationRepository;
    private readonly IConversationMessageRepository _conversationMessageRepository;
    private readonly IAuditEventRepository _auditEventRepository;
    private readonly IReadOnlyCollection<IReviewContinuationHandler> _reviewContinuationHandlers;

    public ReviewTaskService(
        IReviewTaskRepository reviewTaskRepository,
        IAgentOperationRepository operationRepository,
        IConversationRepository conversationRepository,
        IConversationMessageRepository conversationMessageRepository,
        IAuditEventRepository auditEventRepository,
        IEnumerable<IReviewContinuationHandler> reviewContinuationHandlers)
    {
        _reviewTaskRepository = reviewTaskRepository;
        _operationRepository = operationRepository;
        _conversationRepository = conversationRepository;
        _conversationMessageRepository = conversationMessageRepository;
        _auditEventRepository = auditEventRepository;
        _reviewContinuationHandlers = reviewContinuationHandlers.ToArray();
    }

    public async Task<ChatInteractionResult> SubmitAsync(
        string reviewTaskId,
        ReviewDecisionRequest request,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var result = await ApplyDecisionAsync(reviewTaskId, request, context, cancellationToken);
        return result;
    }

    public async Task<ChatInteractionResult> HandleChatDecisionAsync(
        RoutingDecision routingDecision,
        string message,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var reviewTask = await _reviewTaskRepository.GetAsync(routingDecision.ReviewTaskId!, context.TenantId, cancellationToken);
        if (reviewTask is null)
        {
            return new ChatInteractionResult("I couldn't find that review task anymore.", string.Empty);
        }

        if (reviewTask.InteractionMode == ReviewTaskInteractionMode.EditAndSubmit)
        {
            return new ChatInteractionResult(
                $"{reviewTask.Title} needs a reviewed payload, so please open it from the review queue and submit your edits there.",
                reviewTask.ConversationId,
                reviewTask.OperationId);
        }

        var action = message.ToLowerInvariant().Contains("reject", StringComparison.Ordinal)
            ? "Rejected"
            : "Approved";

        var result = await ApplyDecisionAsync(
            reviewTask.Id,
            new ReviewDecisionRequest(action, reviewTask.ProposedPayloadJson, $"Submitted from chat: {message}"),
            context,
            cancellationToken);

        return new ChatInteractionResult(
            action == "Approved"
                ? $"{reviewTask.Title} is approved. I resumed the workflow from the same conversation thread."
                : $"{reviewTask.Title} was rejected. The workflow is paused until the data is corrected.",
            result.ConversationId,
            result.OperationId);
    }

    private async Task<ChatInteractionResult> ApplyDecisionAsync(
        string reviewTaskId,
        ReviewDecisionRequest request,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var reviewTask = await _reviewTaskRepository.GetAsync(reviewTaskId, context.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("Review task not found.");
        var operation = await _operationRepository.GetAsync(reviewTask.OperationId, context.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("Operation not found.");
        var conversation = await _conversationRepository.GetAsync(reviewTask.ConversationId, context.TenantId, cancellationToken)
            ?? throw new InvalidOperationException("Conversation not found.");
        var outcome = ResolveOutcome(reviewTask, request.Action);
        var finalPayloadJson = request.FinalPayloadJson ?? reviewTask.ProposedPayloadJson;

        if (outcome.ContinuesWorkflow && reviewTask.InteractionMode == ReviewTaskInteractionMode.EditAndSubmit)
        {
            try
            {
                using var _ = JsonDocument.Parse(finalPayloadJson);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("The reviewed payload must be valid JSON before the workflow can continue.", exception);
            }
        }

        reviewTask.Status = outcome.Status;
        reviewTask.FinalPayloadJson = finalPayloadJson;
        reviewTask.Notes = request.Notes;
        reviewTask.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _reviewTaskRepository.UpsertAsync(reviewTask, cancellationToken);

        operation.ActiveReviewTaskId = null;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        operation.Status = outcome.ContinuesWorkflow
            ? AgentOperationStatus.Running
            : AgentOperationStatus.ClarificationRequired;
        operation.CurrentStep = outcome.ContinuesWorkflow
            ? "ResumeRequested"
            : reviewTask.Status == ReviewTaskStatus.NeedsChanges
                ? "ReviewNeedsChanges"
                : "ReviewRejected";
        operation.PendingClarification = outcome.ContinuesWorkflow
            ? null
            : outcome.PendingClarification;
        await _operationRepository.UpsertAsync(operation, cancellationToken);

        await _conversationMessageRepository.AddAsync(
            new ConversationMessage
            {
                TenantId = context.TenantId,
                ConversationId = conversation.Id,
                OperationId = operation.Id,
                AuthorId = "system",
                Role = ConversationMessageRole.System,
                Content = outcome.ContinuesWorkflow
                    ? $"{reviewTask.Title} {outcome.ActionLabel}. Resuming {operation.Title}."
                    : $"{reviewTask.Title} {outcome.ActionLabel}. {operation.Title} is paused until the data is corrected.",
                MessageKind = "review"
            },
            cancellationToken);

        await _auditEventRepository.AddAsync(
            new AuditEvent
            {
                TenantId = context.TenantId,
                ConversationId = conversation.Id,
                OperationId = operation.Id,
                EventType = "ReviewDecisionSubmitted",
                ActorType = "User",
                ActorId = context.UserId,
                DataJson = JsonContent.Serialize(new
                {
                    reviewTaskId = reviewTask.Id,
                    action = request.Action,
                    decision = reviewTask.Status.ToString(),
                    payload = reviewTask.FinalPayloadJson
                })
            },
            cancellationToken);

        if (outcome.ContinuesWorkflow)
        {
            var handler = _reviewContinuationHandlers.FirstOrDefault(candidate => candidate.CanHandle(operation, reviewTask));
            if (handler is not null)
            {
                await handler.HandleApprovedAsync(
                    new ReviewContinuationContext(conversation, operation, reviewTask, context),
                    cancellationToken);
            }
        }

        return new ChatInteractionResult(
            outcome.ContinuesWorkflow
                ? $"{reviewTask.Title} {outcome.ActionLabel}. The workflow is continuing from the saved checkpoint."
                : $"{reviewTask.Title} {outcome.ActionLabel}. The workflow is paused for correction.",
            conversation.Id,
            operation.Id);
    }

    private static ReviewOutcome ResolveOutcome(ReviewTask reviewTask, string? action)
    {
        var normalizedAction = action?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedAction))
        {
            normalizedAction = reviewTask.InteractionMode == ReviewTaskInteractionMode.EditAndSubmit
                ? "Submitted"
                : "Approved";
        }

        if (normalizedAction.Equals("Rejected", StringComparison.OrdinalIgnoreCase))
        {
            return new ReviewOutcome(false, ReviewTaskStatus.Rejected, "rejected", "Review was rejected. Update the payload and resubmit the workflow.");
        }

        if (normalizedAction.Equals("NeedsChanges", StringComparison.OrdinalIgnoreCase))
        {
            return new ReviewOutcome(false, ReviewTaskStatus.NeedsChanges, "sent back with changes", "Review needs changes. Update the payload and submit the workflow again.");
        }

        return new ReviewOutcome(
            true,
            ReviewTaskStatus.Approved,
            reviewTask.InteractionMode == ReviewTaskInteractionMode.EditAndSubmit ? "submitted" : "approved",
            null);
    }

    private sealed record ReviewOutcome(
        bool ContinuesWorkflow,
        ReviewTaskStatus Status,
        string ActionLabel,
        string? PendingClarification);
}
