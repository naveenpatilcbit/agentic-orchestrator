using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Auditing;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using ConversationalOrchestration.Domain.Workflows;

namespace ConversationalOrchestration.FundAdministration.Workflows;

public interface IFundAdministrationWorkflowDispatcher
{
    Task StartOnboardingAsync(
        AgentOperation operation,
        ConversationThread conversation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken);

    Task ContinueOnboardingReviewAsync(
        AgentOperation operation,
        ConversationThread conversation,
        string reviewTaskId,
        string reviewType,
        string finalPayloadJson,
        TenantExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed class FundAdministrationWorkflowDispatcher : IFundAdministrationWorkflowDispatcher
{
    private readonly IWorkflowRuntimeService _workflowRuntimeService;
    private readonly IWorkflowPendingRequestRepository _workflowPendingRequestRepository;
    private readonly IReviewTaskRepository _reviewTaskRepository;
    private readonly IAgentOperationRepository _operationRepository;
    private readonly IConversationMessageRepository _conversationMessageRepository;
    private readonly IAuditEventRepository _auditEventRepository;

    public FundAdministrationWorkflowDispatcher(
        IWorkflowRuntimeService workflowRuntimeService,
        IWorkflowPendingRequestRepository workflowPendingRequestRepository,
        IReviewTaskRepository reviewTaskRepository,
        IAgentOperationRepository operationRepository,
        IConversationMessageRepository conversationMessageRepository,
        IAuditEventRepository auditEventRepository)
    {
        _workflowRuntimeService = workflowRuntimeService;
        _workflowPendingRequestRepository = workflowPendingRequestRepository;
        _reviewTaskRepository = reviewTaskRepository;
        _operationRepository = operationRepository;
        _conversationMessageRepository = conversationMessageRepository;
        _auditEventRepository = auditEventRepository;
    }

    public async Task StartOnboardingAsync(
        AgentOperation operation,
        ConversationThread conversation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var result = await _workflowRuntimeService.StartAsync(
            new WorkflowStartRequest(
                context.TenantId,
                conversation.Id,
                operation.Id,
                FundAdministrationWorkflowNames.FundOnboarding,
                new FundOnboardingWorkflowStart(
                    operation.Id,
                    conversation.Id,
                    context.UserId,
                    attachments.Select(asset => asset.Id).ToArray(),
                    attachments.Select(asset => asset.FileName).ToArray())),
            cancellationToken);

        await ApplyOnboardingWorkflowResultAsync(operation, conversation, result, context, cancellationToken);
    }

    public async Task ContinueOnboardingReviewAsync(
        AgentOperation operation,
        ConversationThread conversation,
        string reviewTaskId,
        string reviewType,
        string finalPayloadJson,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var pendingRequest = await ResolvePendingRequestAsync(reviewTaskId, operation, context.TenantId, cancellationToken);
        if (pendingRequest is null)
        {
            throw new InvalidOperationException($"No pending workflow request was found for review task '{reviewTaskId}'.");
        }

        var result = await _workflowRuntimeService.ResumeAsync(
            new WorkflowResumeRequest(
                context.TenantId,
                operation.WorkflowInstanceId ?? throw new InvalidOperationException("Workflow instance id is missing on the operation."),
                pendingRequest.Id,
                finalPayloadJson,
                operation.LatestCheckpointId),
            cancellationToken);

        await ApplyOnboardingWorkflowResultAsync(operation, conversation, result, context, cancellationToken);
    }

    private async Task<WorkflowPendingRequest?> ResolvePendingRequestAsync(
        string reviewTaskId,
        AgentOperation operation,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var reviewTask = await _reviewTaskRepository.GetAsync(reviewTaskId, tenantId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(reviewTask?.WorkflowPendingRequestId))
        {
            return await _workflowPendingRequestRepository.GetAsync(reviewTask.WorkflowPendingRequestId, tenantId, cancellationToken);
        }

        return await _workflowPendingRequestRepository.GetLatestOpenByOperationAsync(operation.Id, tenantId, cancellationToken);
    }

    private async Task ApplyOnboardingWorkflowResultAsync(
        AgentOperation operation,
        ConversationThread conversation,
        WorkflowRunResult result,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        operation.WorkflowInstanceId = result.Instance.Id;
        operation.LatestCheckpointId = result.Instance.LatestCheckpointId;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage) || result.Instance.Status == WorkflowInstanceStatus.Failed)
        {
            operation.Status = AgentOperationStatus.Failed;
            operation.CurrentStep = result.Instance.CurrentStep;
            operation.Summary = result.ErrorMessage ?? "The workflow failed before the next review checkpoint.";
            operation.PendingClarification = "Retry the workflow from the saved checkpoint once the issue is addressed.";
            await _operationRepository.UpsertAsync(operation, cancellationToken);
            await AddConversationUpdateAsync(operation, $"The onboarding workflow hit an error: {operation.Summary}", cancellationToken);
            await _auditEventRepository.AddAsync(
                BuildAuditEvent(operation, "FundOnboardingWorkflowFailed", new { error = result.ErrorMessage }),
                cancellationToken);
            return;
        }

        if (result.PendingRequests.Count > 0)
        {
            var nextRequest = result.PendingRequests.Last();
            var reviewTask = await EnsureReviewTaskAsync(nextRequest, cancellationToken);

            operation.Status = AgentOperationStatus.WaitingForHumanReview;
            operation.CurrentStep = reviewTask.TaskType;
            operation.ActiveReviewTaskId = reviewTask.Id;
            operation.PendingClarification = null;
            operation.Summary = reviewTask.TaskType == "ClassificationReview"
                ? "Document classification is ready for review."
                : "Field extraction is ready for review.";

            await _operationRepository.UpsertAsync(operation, cancellationToken);
            await AddConversationUpdateAsync(
                operation,
                reviewTask.TaskType == "ClassificationReview"
                    ? "Document classification review is ready. Approve it from the review queue to continue."
                    : "Extraction review is ready. Approve the extracted fields to create the draft record.",
                cancellationToken);
            await _auditEventRepository.AddAsync(
                BuildAuditEvent(operation, "FundOnboardingWorkflowWaitingForReview", new
                {
                    workflowInstanceId = result.Instance.Id,
                    reviewTaskId = reviewTask.Id,
                    portId = nextRequest.PortId
                }),
                cancellationToken);
            return;
        }

        var completion = result.Outputs
            .FirstOrDefault(output => string.Equals(output.OutputType, nameof(FundOnboardingWorkflowCompleted), StringComparison.Ordinal));
        if (completion is not null)
        {
            var completedPayload = JsonContent.Deserialize<FundOnboardingWorkflowCompleted>(completion.PayloadJson)
                ?? throw new InvalidOperationException("Workflow completion payload could not be parsed.");

            operation.Status = AgentOperationStatus.Completed;
            operation.CurrentStep = "DraftCreated";
            operation.ActiveReviewTaskId = null;
            operation.PendingClarification = null;
            operation.Summary = "Extraction approved and fund draft created.";
            operation.DataJson = JsonContent.Serialize(new
            {
                fundDraftRoute = completedPayload.FundDraftRoute,
                fundName = completedPayload.FundName,
                workflowInstanceId = result.Instance.Id,
                extractionPayload = completedPayload.ExtractionPayloadJson
            });

            await _operationRepository.UpsertAsync(operation, cancellationToken);
            await AddConversationUpdateAsync(operation, "Extraction approved. The fund draft is now ready for final review.", cancellationToken);
            await _auditEventRepository.AddAsync(
                BuildAuditEvent(operation, "FundOnboardingWorkflowCompleted", new
                {
                    workflowInstanceId = result.Instance.Id,
                    draftRoute = completedPayload.FundDraftRoute,
                    fundName = completedPayload.FundName
                }),
                cancellationToken);
            return;
        }

        operation.Status = AgentOperationStatus.Running;
        operation.CurrentStep = result.Instance.CurrentStep;
        operation.Summary = "The onboarding workflow resumed successfully and is waiting for the next transition.";
        await _operationRepository.UpsertAsync(operation, cancellationToken);
    }

    private async Task<ReviewTask> EnsureReviewTaskAsync(
        WorkflowPendingRequest pendingRequest,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(pendingRequest.ReviewTaskId))
        {
            var existingTask = await _reviewTaskRepository.GetAsync(pendingRequest.ReviewTaskId, pendingRequest.TenantId, cancellationToken);
            if (existingTask is not null)
            {
                existingTask.ProposedPayloadJson = pendingRequest.RequestPayloadJson;
                existingTask.Status = ReviewTaskStatus.Open;
                existingTask.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await _reviewTaskRepository.UpsertAsync(existingTask, cancellationToken);
                return existingTask;
            }
        }

        var reviewTask = new ReviewTask
        {
            TenantId = pendingRequest.TenantId,
            ConversationId = pendingRequest.ConversationId,
            OperationId = pendingRequest.OperationId,
            WorkflowPendingRequestId = pendingRequest.Id,
            Title = pendingRequest.PortId == FundOnboardingWorkflowPorts.ClassificationReview
                ? "Classification Review"
                : "Extraction Review",
            TaskType = pendingRequest.PortId == FundOnboardingWorkflowPorts.ClassificationReview
                ? "ClassificationReview"
                : "ExtractionReview",
            ProposedPayloadJson = pendingRequest.RequestPayloadJson
        };

        pendingRequest.ReviewTaskId = reviewTask.Id;
        pendingRequest.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _reviewTaskRepository.UpsertAsync(reviewTask, cancellationToken);
        await _workflowPendingRequestRepository.UpsertAsync(pendingRequest, cancellationToken);

        return reviewTask;
    }

    private async Task AddConversationUpdateAsync(
        AgentOperation operation,
        string content,
        CancellationToken cancellationToken)
    {
        await _conversationMessageRepository.AddAsync(
            new ConversationMessage
            {
                TenantId = operation.TenantId,
                ConversationId = operation.ConversationId,
                OperationId = operation.Id,
                AuthorId = "workflow",
                Role = ConversationMessageRole.System,
                Content = content,
                MessageKind = "workflow"
            },
            cancellationToken);
    }

    private static AuditEvent BuildAuditEvent(AgentOperation operation, string eventType, object payload) =>
        new()
        {
            TenantId = operation.TenantId,
            ConversationId = operation.ConversationId,
            OperationId = operation.Id,
            EventType = eventType,
            ActorType = "Workflow",
            ActorId = FundAdministrationWorkflowNames.FundOnboarding,
            DataJson = JsonContent.Serialize(payload)
        };
}
