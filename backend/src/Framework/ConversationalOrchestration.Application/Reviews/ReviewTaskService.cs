using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Conversations;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Contracts;
using ConversationalOrchestration.Domain.Auditing;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConversationalOrchestration.Application.Reviews;

public sealed class ReviewTaskService : IReviewTaskService
{
    private readonly IReviewTaskRepository _reviewTaskRepository;
    private readonly IAgentOperationRepository _operationRepository;
    private readonly IConversationRepository _conversationRepository;
    private readonly IConversationTranscriptService _conversationTranscriptService;
    private readonly IAuditEventRepository _auditEventRepository;
    private readonly IReviewPayloadRevisionService _reviewPayloadRevisionService;
    private readonly IReadOnlyCollection<IReviewContinuationHandler> _reviewContinuationHandlers;
    private readonly IPlanContinuationService _planContinuationService;
    private readonly ILogger<ReviewTaskService> _logger;

    public ReviewTaskService(
        IReviewTaskRepository reviewTaskRepository,
        IAgentOperationRepository operationRepository,
        IConversationRepository conversationRepository,
        IConversationTranscriptService conversationTranscriptService,
        IAuditEventRepository auditEventRepository,
        IReviewPayloadRevisionService reviewPayloadRevisionService,
        IPlanContinuationService planContinuationService,
        IEnumerable<IReviewContinuationHandler> reviewContinuationHandlers,
        ILogger<ReviewTaskService> logger)
    {
        _reviewTaskRepository = reviewTaskRepository;
        _operationRepository = operationRepository;
        _conversationRepository = conversationRepository;
        _conversationTranscriptService = conversationTranscriptService;
        _auditEventRepository = auditEventRepository;
        _reviewPayloadRevisionService = reviewPayloadRevisionService;
        _planContinuationService = planContinuationService;
        _reviewContinuationHandlers = reviewContinuationHandlers.ToArray();
        _logger = logger;
    }

    public async Task<ChatInteractionResult> SubmitAsync(
        string reviewTaskId,
        ReviewDecisionRequest request,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var result = await ApplyDecisionAsync(reviewTaskId, request, context, cancellationToken);
        // Load the persisted review task so we can apply post-processing (plan continuation and transcript behavior).
        var updatedReviewTask = await _reviewTaskRepository.GetAsync(reviewTaskId, context.TenantId, cancellationToken);

        // If a multi-step plan is active, approvals should trigger the next master-agent run automatically.
        if (updatedReviewTask is not null && updatedReviewTask.Status == ReviewTaskStatus.Approved)
        {
            var continuation = await _planContinuationService.TryContinueAfterReviewApprovalAsync(
                updatedReviewTask,
                context,
                cancellationToken);

            if (continuation is not null && !string.IsNullOrWhiteSpace(continuation.AssistantMessage))
            {
                await _conversationTranscriptService.AppendAsync(
                    new ConversationTranscriptAppendRequest(
                        context.TenantId,
                        continuation.ConversationId,
                        "system",
                        ConversationMessageRole.Assistant,
                        continuation.AssistantMessage,
                        MessageKind: "plan-continuation",
                        OperationId: continuation.OperationId,
                        Actions: continuation.Actions,
                        SourceType: "plan-continuation",
                        SourceMessageId: reviewTaskId,
                        DeduplicationKey: $"plan-continuation-assistant:{reviewTaskId}"),
                    cancellationToken);

                return continuation;
            }
        }

        // Avoid transcript spam for workflow-driven capital call reviews: the workflow/operation state already
        // surfaces the right next prompt (e.g., "open review queue", "allocations ready for confirmation").
        if (updatedReviewTask is not null &&
            updatedReviewTask.TaskType.StartsWith("CapitalCall", StringComparison.Ordinal))
        {
            return result;
        }

        if (!string.IsNullOrWhiteSpace(result.ConversationId) &&
            !string.IsNullOrWhiteSpace(result.AssistantMessage))
        {
            await _conversationTranscriptService.AppendAsync(
                new ConversationTranscriptAppendRequest(
                    context.TenantId,
                    result.ConversationId,
                    "review-queue",
                    ConversationMessageRole.Assistant,
                    result.AssistantMessage,
                    "review",
                    OperationId: result.OperationId,
                    Actions: result.Actions,
                    SourceType: "review-decision",
                    SourceMessageId: reviewTaskId,
                    DeduplicationKey: $"review-submit:{reviewTaskId}:{request.ClientRequestId ?? Guid.NewGuid().ToString("N")}"),
                cancellationToken);

            _logger.LogInformation(
                "Added assistant review acknowledgement for tenant {TenantId} conversation {ConversationId} operation {OperationId} after review submission.",
                context.TenantId,
                result.ConversationId,
                result.OperationId);
        }

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
            var normalizedAction = DetermineChatAction(reviewTask, message);
            return await ApplyDecisionAsync(
                reviewTask.Id,
                new ReviewDecisionRequest(
                    normalizedAction,
                    FinalPayloadJson: null,
                    ChangeRequestText: normalizedAction == "Submitted" ? message : null,
                    Notes: $"Submitted from chat: {message}"),
                context,
                cancellationToken);
        }

        var action = DetermineChatAction(reviewTask, message);

        var result = await ApplyDecisionAsync(
            reviewTask.Id,
            new ReviewDecisionRequest(action, reviewTask.ProposedPayloadJson, null, $"Submitted from chat: {message}"),
            context,
            cancellationToken);

        if (UsesWorkflowDrivenReviewMessaging(reviewTask))
        {
            return result;
        }

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
        _logger.LogInformation(
            "Applying review decision for tenant {TenantId} conversation {ConversationId} operation {OperationId}. ReviewTaskId={ReviewTaskId} TaskType={TaskType} InteractionMode={InteractionMode} Action={Action} PendingRequestId={PendingRequestId}",
            context.TenantId,
            conversation.Id,
            operation.Id,
            reviewTask.Id,
            reviewTask.TaskType,
            reviewTask.InteractionMode,
            request.Action,
            reviewTask.WorkflowPendingRequestId);
        var outcome = ResolveOutcome(reviewTask, request.Action);
        var payloadResolution = await ResolveFinalPayloadAsync(reviewTask, request, cancellationToken);
        if (!payloadResolution.Success)
        {
            _logger.LogInformation(
                "Review decision for tenant {TenantId} review task {ReviewTaskId} requires clarification before continuing. ClarificationPrompt={ClarificationPrompt}",
                context.TenantId,
                reviewTask.Id,
                payloadResolution.ClarificationPrompt);
            return new ChatInteractionResult(
                payloadResolution.ClarificationPrompt ?? "I couldn't apply those review changes yet.",
                conversation.Id,
                operation.Id);
        }

        var finalPayloadJson = payloadResolution.RevisedPayloadJson ?? reviewTask.ProposedPayloadJson;
        var notes = CombineNotes(request.Notes, payloadResolution.Summary);

        if (outcome.ContinuesWorkflow && reviewTask.InteractionMode == ReviewTaskInteractionMode.EditAndSubmit)
        {
            // Editable review steps always resume the workflow with a structured payload, even when
            // the user answered in natural language. Validate that boundary before continuing.
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
        reviewTask.Notes = notes;
        reviewTask.UpdatedAtUtc = DateTimeOffset.UtcNow;

        operation.ActiveReviewTaskId = null;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        if (outcome.ContinuesWorkflow)
        {
            operation.Status = AgentOperationStatus.Running;
            operation.CurrentStep = "ResumeRequested";
            operation.PendingClarification = null;

            var handler = _reviewContinuationHandlers.FirstOrDefault(candidate => candidate.CanHandle(operation, reviewTask));
            if (handler is null)
            {
                _logger.LogWarning(
                    "Review decision wants to continue workflow but no continuation handler matched. Tenant={TenantId} ConversationId={ConversationId} OperationId={OperationId} AgentId={AgentId} ReviewTaskId={ReviewTaskId} TaskType={TaskType} WorkflowInstanceId={WorkflowInstanceId} CheckpointId={CheckpointId}",
                    context.TenantId,
                    conversation.Id,
                    operation.Id,
                    operation.AgentId,
                    reviewTask.Id,
                    reviewTask.TaskType,
                    operation.WorkflowInstanceId,
                    operation.LatestCheckpointId);
            }
            else
            {
                _logger.LogInformation(
                    "Review decision matched continuation handler {HandlerType}. Tenant={TenantId} ConversationId={ConversationId} OperationId={OperationId} ReviewTaskId={ReviewTaskId} WorkflowInstanceId={WorkflowInstanceId} CheckpointId={CheckpointId}",
                    handler.GetType().Name,
                    context.TenantId,
                    conversation.Id,
                    operation.Id,
                    reviewTask.Id,
                    operation.WorkflowInstanceId,
                    operation.LatestCheckpointId);

                try
                {
                    await handler.HandleApprovedAsync(
                        new ReviewContinuationContext(conversation, operation, reviewTask, context),
                        cancellationToken);
                    _logger.LogInformation(
                        "Review continuation handler {HandlerType} finished resume handoff for tenant {TenantId} operation {OperationId} reviewTask {ReviewTaskId}.",
                        handler.GetType().Name,
                        context.TenantId,
                        operation.Id,
                        reviewTask.Id);
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Review continuation handler {HandlerType} failed for tenant {TenantId} conversation {ConversationId} operation {OperationId} reviewTask {ReviewTaskId}.",
                        handler.GetType().Name,
                        context.TenantId,
                        conversation.Id,
                        operation.Id,
                        reviewTask.Id);
                    throw;
                }
            }
        }
        else
        {
            operation.Status = AgentOperationStatus.ClarificationRequired;
            operation.CurrentStep = reviewTask.Status == ReviewTaskStatus.NeedsChanges
                ? "ReviewNeedsChanges"
                : "ReviewRejected";
            operation.PendingClarification = outcome.PendingClarification;
            _logger.LogInformation(
                "Review decision paused workflow for correction. Tenant={TenantId} ConversationId={ConversationId} OperationId={OperationId} ReviewTaskId={ReviewTaskId} ReviewStatus={ReviewStatus} CurrentStep={CurrentStep}",
                context.TenantId,
                conversation.Id,
                operation.Id,
                reviewTask.Id,
                reviewTask.Status,
                operation.CurrentStep);
        }

        reviewTask.UpdatedAtUtc = DateTimeOffset.UtcNow;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _reviewTaskRepository.UpsertAsync(reviewTask, cancellationToken);
        await _operationRepository.UpsertAsync(operation, cancellationToken);
        if (ConversationSessionStateResolver.SyncConversationPointers(
                conversation,
                ConversationSessionStateResolver.IsActiveOperation(operation) ? operation : null))
        {
            conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _conversationRepository.UpsertAsync(conversation, cancellationToken);
        }

        var echoReviewComment = ShouldEchoCommentToConversation(request);
        if (echoReviewComment)
        {
            await _conversationTranscriptService.AppendAsync(
                new ConversationTranscriptAppendRequest(
                    context.TenantId,
                    conversation.Id,
                    context.UserId,
                    ConversationMessageRole.User,
                    request.ChangeRequestText!,
                    "review-comment",
                    OperationId: operation.Id,
                    SourceType: "review-comment",
                    SourceMessageId: reviewTask.Id,
                    DeduplicationKey: $"review-comment:{reviewTask.Id}:{request.ClientRequestId ?? Guid.NewGuid().ToString("N")}"),
                cancellationToken);
        }

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
                    payload = reviewTask.FinalPayloadJson,
                    changeRequestText = request.ChangeRequestText
                })
            },
            cancellationToken);

        _logger.LogInformation(
            "Review decision persisted for tenant {TenantId} conversation {ConversationId} operation {OperationId}. ReviewTaskId={ReviewTaskId} ReviewStatus={ReviewStatus} OperationStatus={OperationStatus} CurrentStep={CurrentStep}",
            context.TenantId,
            conversation.Id,
            operation.Id,
            reviewTask.Id,
            reviewTask.Status,
            operation.Status,
            operation.CurrentStep);

        return new ChatInteractionResult(
            ResolveAssistantMessage(reviewTask, outcome, operation),
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

    private static bool UsesWorkflowDrivenReviewMessaging(ReviewTask reviewTask) =>
        reviewTask.TaskType.StartsWith("CapitalCall", StringComparison.Ordinal);

    private static string ResolveAssistantMessage(
        ReviewTask reviewTask,
        ReviewOutcome outcome,
        AgentOperation operation)
    {
        if (UsesWorkflowDrivenReviewMessaging(reviewTask))
        {
            if (!string.IsNullOrWhiteSpace(operation.PendingClarification))
            {
                return operation.PendingClarification;
            }

            var resumedToConcreteState =
                operation.Status != AgentOperationStatus.Running ||
                !string.Equals(operation.CurrentStep, "ResumeRequested", StringComparison.Ordinal);

            if (resumedToConcreteState && !string.IsNullOrWhiteSpace(operation.Summary))
            {
                return operation.Summary;
            }
        }

        return outcome.ContinuesWorkflow
            ? $"{reviewTask.Title} {outcome.ActionLabel}. The workflow is continuing from the saved checkpoint."
            : $"{reviewTask.Title} {outcome.ActionLabel}. The workflow is paused for correction.";
    }

    private async Task<ReviewPayloadRevisionResult> ResolveFinalPayloadAsync(
        ReviewTask reviewTask,
        ReviewDecisionRequest request,
        CancellationToken cancellationToken)
    {
        var basePayloadJson = request.FinalPayloadJson ?? reviewTask.ProposedPayloadJson;
        // Resolution order is: explicit raw edit -> natural-language revision -> original payload.
        // This keeps edit-and-submit tasks generic across different agents.
        if (string.IsNullOrWhiteSpace(request.ChangeRequestText) ||
            !ResolveOutcome(reviewTask, request.Action).ContinuesWorkflow ||
            reviewTask.InteractionMode != ReviewTaskInteractionMode.EditAndSubmit)
        {
            var mergedPayloadJson = MergePayloads(reviewTask.ProposedPayloadJson, basePayloadJson);
            return new ReviewPayloadRevisionResult(true, RevisedPayloadJson: mergedPayloadJson);
        }

        if (!string.IsNullOrWhiteSpace(request.FinalPayloadJson))
        {
            var mergedPayloadJson = MergePayloads(reviewTask.ProposedPayloadJson, request.FinalPayloadJson);
            return new ReviewPayloadRevisionResult(true, RevisedPayloadJson: mergedPayloadJson);
        }

        var revision = await _reviewPayloadRevisionService.ReviseAsync(
            reviewTask,
            reviewTask.ProposedPayloadJson,
            request.ChangeRequestText,
            cancellationToken);
        if (!revision.Success || string.IsNullOrWhiteSpace(revision.RevisedPayloadJson))
        {
            return revision;
        }

        return revision with
        {
            RevisedPayloadJson = MergePayloads(reviewTask.ProposedPayloadJson, revision.RevisedPayloadJson)
        };
    }

    private static string DetermineChatAction(ReviewTask reviewTask, string message)
    {
        if (message.Contains("reject", StringComparison.OrdinalIgnoreCase))
        {
            return "Rejected";
        }

        if (reviewTask.InteractionMode == ReviewTaskInteractionMode.EditAndSubmit &&
            !message.Contains("approve", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("go ahead", StringComparison.OrdinalIgnoreCase))
        {
            return "Submitted";
        }

        return "Approved";
    }

    private static string? CombineNotes(string? originalNotes, string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return originalNotes;
        }

        if (string.IsNullOrWhiteSpace(originalNotes))
        {
            return summary;
        }

        return $"{originalNotes} | {summary}";
    }

    private static bool ShouldEchoCommentToConversation(ReviewDecisionRequest request) =>
        !string.IsNullOrWhiteSpace(request.ChangeRequestText) &&
        (string.IsNullOrWhiteSpace(request.Notes) ||
         !request.Notes.StartsWith("Submitted from chat:", StringComparison.OrdinalIgnoreCase));

    private static string MergePayloads(string originalPayloadJson, string? revisedPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(revisedPayloadJson))
        {
            return originalPayloadJson;
        }

        JsonNode? originalNode;
        JsonNode? revisedNode;
        try
        {
            originalNode = JsonNode.Parse(originalPayloadJson);
            revisedNode = JsonNode.Parse(revisedPayloadJson);
        }
        catch (JsonException)
        {
            return revisedPayloadJson;
        }

        if (originalNode is not JsonObject originalObject || revisedNode is not JsonObject revisedObject)
        {
            return revisedPayloadJson;
        }

        // Review edits are often partial. Merge them over the proposed payload so metadata the user
        // never touched, like request-state fields needed for workflow resume, does not get dropped.
        var merged = MergeObjects(originalObject, revisedObject);
        return merged.ToJsonString();
    }

    private static JsonObject MergeObjects(JsonObject original, JsonObject revised)
    {
        var merged = new JsonObject();

        foreach (var property in original)
        {
            merged[property.Key] = property.Value?.DeepClone();
        }

        foreach (var property in revised)
        {
            if (property.Value is JsonObject revisedChild &&
                merged[property.Key] is JsonObject originalChild)
            {
                merged[property.Key] = MergeObjects(originalChild, revisedChild);
                continue;
            }

            merged[property.Key] = property.Value?.DeepClone();
        }

        return merged;
    }
}
