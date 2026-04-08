using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Contracts;
using ConversationalOrchestration.Domain.Auditing;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;

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

        var decision = message.ToLowerInvariant().Contains("reject", StringComparison.Ordinal)
            ? "Rejected"
            : "Approved";

        var result = await ApplyDecisionAsync(
            reviewTask.Id,
            new ReviewDecisionRequest(decision, reviewTask.ProposedPayloadJson, $"Submitted from chat: {message}"),
            context,
            cancellationToken);

        return new ChatInteractionResult(
            decision == "Approved"
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

        reviewTask.Status = request.Decision.Equals("Approved", StringComparison.OrdinalIgnoreCase)
            ? ReviewTaskStatus.Approved
            : ReviewTaskStatus.Rejected;
        reviewTask.FinalPayloadJson = request.FinalPayloadJson ?? reviewTask.ProposedPayloadJson;
        reviewTask.Notes = request.Notes;
        reviewTask.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _reviewTaskRepository.UpsertAsync(reviewTask, cancellationToken);

        operation.ActiveReviewTaskId = null;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        operation.Status = reviewTask.Status == ReviewTaskStatus.Approved
            ? AgentOperationStatus.Running
            : AgentOperationStatus.ClarificationRequired;
        operation.CurrentStep = reviewTask.Status == ReviewTaskStatus.Approved
            ? "ResumeRequested"
            : "ReviewRejected";
        operation.PendingClarification = reviewTask.Status == ReviewTaskStatus.Approved
            ? null
            : "Review was rejected. Update the payload and resubmit the workflow.";
        await _operationRepository.UpsertAsync(operation, cancellationToken);

        await _conversationMessageRepository.AddAsync(
            new ConversationMessage
            {
                TenantId = context.TenantId,
                ConversationId = conversation.Id,
                OperationId = operation.Id,
                AuthorId = "system",
                Role = ConversationMessageRole.System,
                Content = reviewTask.Status == ReviewTaskStatus.Approved
                    ? $"{reviewTask.Title} approved. Resuming {operation.Title}."
                    : $"{reviewTask.Title} rejected. {operation.Title} is paused until the data is corrected.",
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
                    decision = reviewTask.Status.ToString(),
                    payload = reviewTask.FinalPayloadJson
                })
            },
            cancellationToken);

        if (reviewTask.Status == ReviewTaskStatus.Approved)
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
            reviewTask.Status == ReviewTaskStatus.Approved
                ? $"{reviewTask.Title} approved. The workflow is continuing from the saved checkpoint."
                : $"{reviewTask.Title} rejected. The workflow is paused for correction.",
            conversation.Id,
            operation.Id);
    }
}
