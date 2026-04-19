using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Auditing;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using ConversationalOrchestration.Domain.Workflows;
using ConversationalOrchestration.FundAdministration.CapitalCalls;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace ConversationalOrchestration.FundAdministration.Workflows;

public interface IFundAdministrationWorkflowDispatcher
{
    Task StartCapitalCallNoticeAsync(
        AgentOperation operation,
        ConversationThread conversation,
        string initialUserMessage,
        TenantExecutionContext context,
        CancellationToken cancellationToken);

    Task ContinueCapitalCallNoticeAsync(
        AgentOperation operation,
        ConversationThread conversation,
        string clarificationMessage,
        TenantExecutionContext context,
        CancellationToken cancellationToken);

    Task ContinueCapitalCallReviewAsync(
        AgentOperation operation,
        ConversationThread conversation,
        string reviewTaskId,
        string finalPayloadJson,
        TenantExecutionContext context,
        CancellationToken cancellationToken);

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
    private readonly ICapitalCallConversationIntelligence _capitalCallConversationIntelligence;
    private readonly ICapitalCallRequestPreparationService _capitalCallRequestPreparationService;
    private readonly IWorkflowRuntimeService _workflowRuntimeService;
    private readonly IWorkflowPendingRequestRepository _workflowPendingRequestRepository;
    private readonly IReviewTaskRepository _reviewTaskRepository;
    private readonly IAgentOperationRepository _operationRepository;
    private readonly IOperationOutputRepository _operationOutputRepository;
    private readonly IConversationTranscriptService _conversationTranscriptService;
    private readonly IAuditEventRepository _auditEventRepository;
    private readonly ILogger<FundAdministrationWorkflowDispatcher> _logger;

    public FundAdministrationWorkflowDispatcher(
        ICapitalCallConversationIntelligence capitalCallConversationIntelligence,
        ICapitalCallRequestPreparationService capitalCallRequestPreparationService,
        IWorkflowRuntimeService workflowRuntimeService,
        IWorkflowPendingRequestRepository workflowPendingRequestRepository,
        IReviewTaskRepository reviewTaskRepository,
        IAgentOperationRepository operationRepository,
        IOperationOutputRepository operationOutputRepository,
        IConversationTranscriptService conversationTranscriptService,
        IAuditEventRepository auditEventRepository,
        ILogger<FundAdministrationWorkflowDispatcher> logger)
    {
        _capitalCallConversationIntelligence = capitalCallConversationIntelligence;
        _capitalCallRequestPreparationService = capitalCallRequestPreparationService;
        _workflowRuntimeService = workflowRuntimeService;
        _workflowPendingRequestRepository = workflowPendingRequestRepository;
        _reviewTaskRepository = reviewTaskRepository;
        _operationRepository = operationRepository;
        _operationOutputRepository = operationOutputRepository;
        _conversationTranscriptService = conversationTranscriptService;
        _auditEventRepository = auditEventRepository;
        _logger = logger;
    }

    public async Task StartCapitalCallNoticeAsync(
        AgentOperation operation,
        ConversationThread conversation,
        string initialUserMessage,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting capital call notice intake for tenant {TenantId} conversation {ConversationId} operation {OperationId}.",
            context.TenantId,
            conversation.Id,
            operation.Id);
        var patch = await _capitalCallConversationIntelligence.CaptureIntentAsync(
            context.TenantId,
            conversation.Id,
            operation.Id,
            initialUserMessage,
            cancellationToken);

        var preparation = await _capitalCallRequestPreparationService.PrepareAsync(
            context.TenantId,
            conversation.Id,
            SerializeCapitalCallRequestState(operation),
            patch,
            cancellationToken);

        if (!preparation.IsReady)
        {
            _logger.LogInformation(
                "Capital call operation {OperationId} requires clarification before workflow start. Prompt={Prompt}",
                operation.Id,
                preparation.ClarificationPrompt);
            await ApplyCapitalCallClarificationStateAsync(operation, preparation, cancellationToken);
            return;
        }

        await StartCapitalCallExecutionWorkflowAsync(operation, conversation, preparation, context, cancellationToken);
    }

    public async Task ContinueCapitalCallNoticeAsync(
        AgentOperation operation,
        ConversationThread conversation,
        string clarificationMessage,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Continuing capital call notice intake for tenant {TenantId} conversation {ConversationId} operation {OperationId}.",
            context.TenantId,
            conversation.Id,
            operation.Id);
        var patch = await _capitalCallConversationIntelligence.InterpretClarificationAsync(
            context.TenantId,
            conversation.Id,
            operation.Id,
            clarificationMessage,
            cancellationToken);

        var preparation = await _capitalCallRequestPreparationService.PrepareAsync(
            context.TenantId,
            conversation.Id,
            SerializeCapitalCallRequestState(operation),
            patch,
            cancellationToken);

        if (!preparation.IsReady)
        {
            _logger.LogInformation(
                "Capital call operation {OperationId} still requires clarification. Prompt={Prompt}",
                operation.Id,
                preparation.ClarificationPrompt);
            await ApplyCapitalCallClarificationStateAsync(operation, preparation, cancellationToken);
            return;
        }

        await StartCapitalCallExecutionWorkflowAsync(operation, conversation, preparation, context, cancellationToken);
    }

    public async Task ContinueCapitalCallReviewAsync(
        AgentOperation operation,
        ConversationThread conversation,
        string reviewTaskId,
        string finalPayloadJson,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Continuing capital call review for tenant {TenantId} conversation {ConversationId} operation {OperationId}. ReviewTaskId={ReviewTaskId}",
            context.TenantId,
            conversation.Id,
            operation.Id,
            reviewTaskId);

        var pendingRequest = await ResolvePendingRequestAsync(reviewTaskId, operation, context.TenantId, cancellationToken);
        if (pendingRequest is null)
        {
            _logger.LogError(
                "Capital call review could not resolve pending workflow request for tenant {TenantId} operation {OperationId} reviewTask {ReviewTaskId}.",
                context.TenantId,
                operation.Id,
                reviewTaskId);
            throw new InvalidOperationException($"No pending workflow request was found for review task '{reviewTaskId}'.");
        }

        _logger.LogInformation(
            "Capital call review resolved workflow pending request for tenant {TenantId} conversation {ConversationId} operation {OperationId}. ReviewTaskId={ReviewTaskId} PendingRequestId={PendingRequestId} PendingStatus={PendingStatus} PortId={PortId} RequestId={RequestId} RequestType={RequestType} WorkflowInstanceId={WorkflowInstanceId} CheckpointId={CheckpointId}",
            context.TenantId,
            conversation.Id,
            operation.Id,
            reviewTaskId,
            pendingRequest.Id,
            pendingRequest.Status,
            pendingRequest.PortId,
            pendingRequest.RequestId,
            pendingRequest.RequestType,
            operation.WorkflowInstanceId,
            operation.LatestCheckpointId);

        var result = await _workflowRuntimeService.ResumeAsync(
            new WorkflowResumeRequest(
                context.TenantId,
                operation.WorkflowInstanceId ?? throw new InvalidOperationException("Workflow instance id is missing on the operation."),
                pendingRequest.Id,
                finalPayloadJson,
                operation.LatestCheckpointId),
            cancellationToken);

        _logger.LogInformation(
            "Capital call review resume returned for tenant {TenantId} conversation {ConversationId} operation {OperationId}. WorkflowInstanceId={WorkflowInstanceId} WorkflowStatus={WorkflowStatus} CurrentStep={CurrentStep} CheckpointId={CheckpointId} PendingRequests={PendingRequestCount} Outputs={OutputCount} Error={Error}",
            context.TenantId,
            conversation.Id,
            operation.Id,
            result.Instance.Id,
            result.Instance.Status,
            result.Instance.CurrentStep,
            result.Instance.LatestCheckpointId,
            result.PendingRequests.Count,
            result.Outputs.Count,
            result.ErrorMessage);

        await ApplyCapitalCallWorkflowResultAsync(operation, result, context, cancellationToken);
    }

    public async Task StartOnboardingAsync(
        AgentOperation operation,
        ConversationThread conversation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting onboarding workflow for tenant {TenantId} conversation {ConversationId} operation {OperationId}. Attachments={AttachmentCount}",
            context.TenantId,
            conversation.Id,
            operation.Id,
            attachments.Count);
        var result = await _workflowRuntimeService.StartAsync(
            new WorkflowStartRequest<FundOnboardingWorkflowStart>(
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
        _logger.LogInformation(
            "Continuing onboarding review for tenant {TenantId} conversation {ConversationId} operation {OperationId}. ReviewTaskId={ReviewTaskId} ReviewType={ReviewType}",
            context.TenantId,
            conversation.Id,
            operation.Id,
            reviewTaskId,
            reviewType);
        var pendingRequest = await ResolvePendingRequestAsync(reviewTaskId, operation, context.TenantId, cancellationToken);
        if (pendingRequest is null)
        {
            _logger.LogError(
                "Pending workflow request could not be resolved for tenant {TenantId} operation {OperationId} reviewTask {ReviewTaskId}.",
                context.TenantId,
                operation.Id,
                reviewTaskId);
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
            var linkedPendingRequest = await _workflowPendingRequestRepository.GetAsync(reviewTask.WorkflowPendingRequestId, tenantId, cancellationToken);
            if (linkedPendingRequest is not null)
            {
                _logger.LogInformation(
                    "Resolved workflow pending request from review task link for tenant {TenantId} operation {OperationId}. ReviewTaskId={ReviewTaskId} PendingRequestId={PendingRequestId} PortId={PortId} Status={Status}",
                    tenantId,
                    operation.Id,
                    reviewTaskId,
                    linkedPendingRequest.Id,
                    linkedPendingRequest.PortId,
                    linkedPendingRequest.Status);
                return linkedPendingRequest;
            }

            _logger.LogWarning(
                "Review task {ReviewTaskId} referenced workflow pending request {PendingRequestId}, but that request was not found. Tenant={TenantId} OperationId={OperationId}",
                reviewTaskId,
                reviewTask.WorkflowPendingRequestId,
                tenantId,
                operation.Id);
        }

        var fallbackPendingRequest = await _workflowPendingRequestRepository.GetLatestOpenByOperationAsync(operation.Id, tenantId, cancellationToken);
        if (fallbackPendingRequest is null)
        {
            _logger.LogWarning(
                "No open workflow pending request was found while resolving review task {ReviewTaskId}. Tenant={TenantId} OperationId={OperationId}",
                reviewTaskId,
                tenantId,
                operation.Id);
            return null;
        }

        _logger.LogInformation(
            "Resolved workflow pending request from latest open operation request for tenant {TenantId} operation {OperationId}. ReviewTaskId={ReviewTaskId} PendingRequestId={PendingRequestId} PortId={PortId} Status={Status}",
            tenantId,
            operation.Id,
            reviewTaskId,
            fallbackPendingRequest.Id,
            fallbackPendingRequest.PortId,
            fallbackPendingRequest.Status);
        return fallbackPendingRequest;
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
            _logger.LogError(
                "Onboarding workflow failed for tenant {TenantId} conversation {ConversationId} operation {OperationId} workflowInstance {WorkflowInstanceId}. Error={Error}",
                context.TenantId,
                conversation.Id,
                operation.Id,
                result.Instance.Id,
                result.ErrorMessage);
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
            _logger.LogInformation(
                "Onboarding workflow waiting for review for tenant {TenantId} conversation {ConversationId} operation {OperationId}. WorkflowInstanceId={WorkflowInstanceId} ReviewTaskId={ReviewTaskId} PortId={PortId}",
                context.TenantId,
                conversation.Id,
                operation.Id,
                result.Instance.Id,
                reviewTask.Id,
                nextRequest.PortId);

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
            if (!completion.TryGetPayload<FundOnboardingWorkflowCompleted>(out var completedPayload) || completedPayload is null)
            {
                throw new InvalidOperationException("Workflow completion payload could not be parsed.");
            }
            _logger.LogInformation(
                "Onboarding workflow completed for tenant {TenantId} conversation {ConversationId} operation {OperationId}. WorkflowInstanceId={WorkflowInstanceId} FundName={FundName}",
                context.TenantId,
                conversation.Id,
                operation.Id,
                result.Instance.Id,
                completedPayload.FundName);

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
        _logger.LogInformation(
            "Onboarding workflow resumed for tenant {TenantId} conversation {ConversationId} operation {OperationId}. WorkflowInstanceId={WorkflowInstanceId} CurrentStep={CurrentStep}",
            context.TenantId,
            conversation.Id,
            operation.Id,
            result.Instance.Id,
            result.Instance.CurrentStep);
    }

    private async Task ApplyCapitalCallWorkflowResultAsync(
        AgentOperation operation,
        WorkflowRunResult result,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        operation.WorkflowInstanceId = result.Instance.Id;
        operation.LatestCheckpointId = result.Instance.LatestCheckpointId;
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        _logger.LogInformation(
            "Applying capital call workflow result for tenant {TenantId} operation {OperationId}. WorkflowInstanceId={WorkflowInstanceId} WorkflowStatus={WorkflowStatus} CurrentStep={CurrentStep} CheckpointId={CheckpointId} PendingRequests={PendingRequestCount} Outputs={OutputCount} Error={Error}",
            context.TenantId,
            operation.Id,
            result.Instance.Id,
            result.Instance.Status,
            result.Instance.CurrentStep,
            result.Instance.LatestCheckpointId,
            result.PendingRequests.Count,
            result.Outputs.Count,
            result.ErrorMessage);

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage) || result.Instance.Status == WorkflowInstanceStatus.Failed)
        {
            _logger.LogError(
                "Capital call workflow failed for tenant {TenantId} operation {OperationId} workflowInstance {WorkflowInstanceId}. Error={Error}",
                context.TenantId,
                operation.Id,
                result.Instance.Id,
                result.ErrorMessage);
            operation.Status = AgentOperationStatus.Failed;
            operation.CurrentStep = result.Instance.CurrentStep;
            operation.PendingClarification = null;
            operation.Summary = result.ErrorMessage ?? "The capital call workflow failed before completion.";
            await _operationRepository.UpsertAsync(operation, cancellationToken);
            await _auditEventRepository.AddAsync(
                BuildCapitalCallAuditEvent(operation, "CapitalCallWorkflowFailed", new
                {
                    workflowInstanceId = result.Instance.Id,
                    error = result.ErrorMessage
                }),
                cancellationToken);
            return;
        }

        // Final approval can yield the completion payload in the same resume cycle. Honor that
        // before reopening any human-review state so we do not bounce back into confirmation.
        var completion = result.Outputs
            .FirstOrDefault(output => string.Equals(output.OutputType, nameof(CapitalCallWorkflowCompleted), StringComparison.Ordinal));
        if (completion is not null)
        {
            if (!completion.TryGetPayload<CapitalCallWorkflowCompleted>(out var completedPayload) || completedPayload is null)
            {
                throw new InvalidOperationException("Capital call workflow completion payload could not be parsed.");
            }

            var publishedOutput = await PublishCapitalCallOutputAsync(operation, completedPayload, cancellationToken);

            operation.Status = AgentOperationStatus.Completed;
            operation.CurrentStep = "TemplateRendering";
            operation.PendingClarification = null;
            operation.ActiveReviewTaskId = null;
            operation.Summary = "Final capital call allocations are confirmed. Template rendering is the current step.";
            operation.DataJson = publishedOutput.PayloadJson;
            await _operationRepository.UpsertAsync(operation, cancellationToken);
        }

        else if (result.PendingRequests.Count > 0)
        {
            var nextRequest = result.PendingRequests.Last();
            if (string.Equals(nextRequest.PortId, CapitalCallNoticeWorkflowPorts.ExtractionReview, StringComparison.Ordinal))
            {
                var reviewTask = await EnsureReviewTaskAsync(nextRequest, cancellationToken);
                _logger.LogInformation(
                    "Capital call workflow waiting for human review for tenant {TenantId} operation {OperationId}. WorkflowInstanceId={WorkflowInstanceId} ReviewTaskId={ReviewTaskId}",
                    context.TenantId,
                    operation.Id,
                    result.Instance.Id,
                    reviewTask.Id);

                operation.Status = AgentOperationStatus.WaitingForHumanReview;
                operation.CurrentStep = reviewTask.TaskType;
                operation.PendingClarification = null;
                operation.ActiveReviewTaskId = reviewTask.Id;
                operation.Summary = "Extracted partner and feeder commitment data is ready for review. Download the workbook, edit the payload if needed, then submit it so the allocation engine can continue.";
                await _operationRepository.UpsertAsync(operation, cancellationToken);
                await _auditEventRepository.AddAsync(
                    BuildCapitalCallAuditEvent(operation, "CapitalCallReviewRequested", new
                    {
                        workflowInstanceId = result.Instance.Id,
                        reviewTaskId = reviewTask.Id,
                        pendingRequestId = nextRequest.Id,
                        portId = nextRequest.PortId
                    }),
                    cancellationToken);
            }
            else if (string.Equals(nextRequest.PortId, CapitalCallNoticeWorkflowPorts.AllocationConfirmation, StringComparison.Ordinal))
            {
                var reviewTask = await EnsureReviewTaskAsync(nextRequest, cancellationToken);
                _logger.LogInformation(
                    "Capital call workflow waiting for final allocation confirmation for tenant {TenantId} operation {OperationId}. WorkflowInstanceId={WorkflowInstanceId} ReviewTaskId={ReviewTaskId}",
                    context.TenantId,
                    operation.Id,
                    result.Instance.Id,
                    reviewTask.Id);

                operation.Status = AgentOperationStatus.WaitingForHumanReview;
                operation.CurrentStep = reviewTask.TaskType;
                operation.PendingClarification = null;
                operation.ActiveReviewTaskId = reviewTask.Id;
                operation.Summary = "Final capital call allocations are ready for confirmation. Review the computed amounts and approve them to finish the workflow.";
                await _operationRepository.UpsertAsync(operation, cancellationToken);
                await _auditEventRepository.AddAsync(
                    BuildCapitalCallAuditEvent(operation, "CapitalCallAllocationConfirmationRequested", new
                    {
                        workflowInstanceId = result.Instance.Id,
                        reviewTaskId = reviewTask.Id,
                        pendingRequestId = nextRequest.Id,
                        portId = nextRequest.PortId
                    }),
                    cancellationToken);
            }
            else
            {
                var clarificationPrompt = ResolveCapitalCallClarificationPrompt(result, nextRequest);
                _logger.LogInformation(
                    "Capital call workflow waiting for clarification for tenant {TenantId} operation {OperationId}. WorkflowInstanceId={WorkflowInstanceId} PendingRequestId={PendingRequestId} PortId={PortId}",
                    context.TenantId,
                    operation.Id,
                    result.Instance.Id,
                    nextRequest.Id,
                    nextRequest.PortId);
                operation.Status = AgentOperationStatus.ClarificationRequired;
                operation.CurrentStep = nextRequest.PortId;
                operation.PendingClarification = clarificationPrompt;
                operation.ActiveReviewTaskId = null;
                operation.Summary = "Waiting for additional capital call details in the same chat thread.";
                await _operationRepository.UpsertAsync(operation, cancellationToken);
                await _auditEventRepository.AddAsync(
                    BuildCapitalCallAuditEvent(operation, "CapitalCallClarificationRequested", new
                    {
                        workflowInstanceId = result.Instance.Id,
                        pendingRequestId = nextRequest.Id,
                        portId = nextRequest.PortId
                    }),
                    cancellationToken);
            }

            return;
        }

        if (completion is null)
        {
            var persistedOperation = await _operationRepository.GetAsync(operation.Id, context.TenantId, cancellationToken);
            if (persistedOperation is not null && HasWorkflowManagedCapitalCallState(persistedOperation))
            {
                CopyOperationState(persistedOperation, operation);
            }
            else
            {
                operation.Status = result.Instance.Status == WorkflowInstanceStatus.Completed
                    ? AgentOperationStatus.Completed
                    : AgentOperationStatus.Running;
                operation.CurrentStep = result.Instance.CurrentStep;
                operation.PendingClarification = null;
                operation.Summary = string.IsNullOrWhiteSpace(operation.Summary)
                    ? result.Instance.Status == WorkflowInstanceStatus.Completed
                        ? "The capital call workflow completed successfully."
                        : "The capital call workflow is running."
                    : operation.Summary;
                await _operationRepository.UpsertAsync(operation, cancellationToken);
            }
        }

        await _auditEventRepository.AddAsync(
            BuildCapitalCallAuditEvent(operation, "CapitalCallWorkflowCompleted", new
            {
                workflowInstanceId = result.Instance.Id,
                checkpointId = result.Instance.LatestCheckpointId
            }),
            cancellationToken);
        _logger.LogInformation(
            "Capital call workflow completed or advanced for tenant {TenantId} operation {OperationId}. WorkflowInstanceId={WorkflowInstanceId} Status={Status} CurrentStep={CurrentStep}",
            context.TenantId,
            operation.Id,
            result.Instance.Id,
            operation.Status,
            operation.CurrentStep);
    }

    private async Task ApplyCapitalCallClarificationStateAsync(
        AgentOperation operation,
        CapitalCallPreparationResult preparation,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Applying capital call clarification state for tenant {TenantId} operation {OperationId}. Prompt={Prompt}",
            operation.TenantId,
            operation.Id,
            preparation.ClarificationPrompt);
        operation.Status = AgentOperationStatus.ClarificationRequired;
        operation.CurrentStep = "CaptureInputs";
        operation.WorkflowInstanceId = null;
        operation.LatestCheckpointId = null;
        operation.PendingClarification = preparation.ClarificationPrompt
            ?? "I still need the remaining capital call details before I can continue.";
        operation.ActiveReviewTaskId = null;
        operation.Summary = "Waiting for additional capital call details in the same chat thread.";
        operation.DataJson = JsonContent.Serialize(new CapitalCallOperationData
        {
            FundName = preparation.RequestState.FundName ?? string.Empty,
            RequestState = preparation.RequestState
        });

        await _operationRepository.UpsertAsync(operation, cancellationToken);
        await _auditEventRepository.AddAsync(
            BuildCapitalCallAuditEvent(operation, "CapitalCallClarificationRequested", new
            {
                clarificationPrompt = operation.PendingClarification,
                requestState = preparation.RequestState
            }),
            cancellationToken);
    }

    private async Task StartCapitalCallExecutionWorkflowAsync(
        AgentOperation operation,
        ConversationThread conversation,
        CapitalCallPreparationResult preparation,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Starting capital call workflow for tenant {TenantId} conversation {ConversationId} operation {OperationId}. Profile={Profile} FundName={FundName} Amount={Amount}",
            context.TenantId,
            conversation.Id,
            operation.Id,
            preparation.RequestState.Profile,
            preparation.RequestState.FundName,
            preparation.RequestState.CapitalCallAmount);

        operation.Status = AgentOperationStatus.Running;
        operation.CurrentStep = "CapitalCallWorkflow";
        operation.PendingClarification = null;
        operation.ActiveReviewTaskId = null;
        operation.Summary = preparation.Summary;
        // Persist the normalized request state on the operation before the workflow starts so the
        // same state can survive clarification loops, review round-trips, and later template output.
        operation.DataJson = JsonContent.Serialize(new CapitalCallOperationData
        {
            FundName = preparation.RequestState.FundName ?? string.Empty,
            RequestState = preparation.RequestState
        });

         var result = await _workflowRuntimeService.StartAsync(
            new WorkflowStartRequest<CapitalCallWorkflowStart>(
                context.TenantId,
                conversation.Id,
                operation.Id,
                FundAdministrationWorkflowNames.CapitalCallNotice,
                new CapitalCallWorkflowStart
                {
                    TenantId = context.TenantId,
                    ConversationId = conversation.Id,
                    OperationId = operation.Id,
                    RequestState = preparation.RequestState
                }),
            cancellationToken);

        await ApplyCapitalCallWorkflowResultAsync(operation, result, context, cancellationToken);
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
                _logger.LogDebug(
                    "Reusing existing review task {ReviewTaskId} for workflow pending request {PendingRequestId}.",
                    existingTask.Id,
                    pendingRequest.Id);
                existingTask.ProposedPayloadJson = pendingRequest.RequestPayloadJson;
                existingTask.Title = ResolveReviewTaskTitle(pendingRequest.PortId);
                existingTask.TaskType = ResolveReviewTaskType(pendingRequest.PortId);
                existingTask.InteractionMode = ResolveReviewTaskInteractionMode(pendingRequest.PortId);
                existingTask.InstructionText = ResolveReviewTaskInstruction(pendingRequest.PortId);
                existingTask.Status = ReviewTaskStatus.Open;
                existingTask.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await _reviewTaskRepository.UpsertAsync(existingTask, cancellationToken);
                return existingTask;
            }
        }

        // The workflow runtime stores a generic pending request. The review task is the product-
        // facing projection of that request that the UI can render and the user can act on.
        var reviewTask = new ReviewTask
        {
            TenantId = pendingRequest.TenantId,
            ConversationId = pendingRequest.ConversationId,
            OperationId = pendingRequest.OperationId,
            WorkflowPendingRequestId = pendingRequest.Id,
            Title = ResolveReviewTaskTitle(pendingRequest.PortId),
            TaskType = ResolveReviewTaskType(pendingRequest.PortId),
            InteractionMode = ResolveReviewTaskInteractionMode(pendingRequest.PortId),
            InstructionText = ResolveReviewTaskInstruction(pendingRequest.PortId),
            ProposedPayloadJson = pendingRequest.RequestPayloadJson
        };

        pendingRequest.ReviewTaskId = reviewTask.Id;
        pendingRequest.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _reviewTaskRepository.UpsertAsync(reviewTask, cancellationToken);
        await _workflowPendingRequestRepository.UpsertAsync(pendingRequest, cancellationToken);
        _logger.LogInformation(
            "Created review task {ReviewTaskId} for workflow pending request {PendingRequestId} on operation {OperationId}.",
            reviewTask.Id,
            pendingRequest.Id,
            pendingRequest.OperationId);

        return reviewTask;
    }

    private async Task AddConversationUpdateAsync(
        AgentOperation operation,
        string content,
        CancellationToken cancellationToken)
    {
        await _conversationTranscriptService.AppendAsync(
            new ConversationTranscriptAppendRequest(
                operation.TenantId,
                operation.ConversationId,
                "workflow",
                ConversationMessageRole.System,
                content,
                "workflow",
                OperationId: operation.Id,
                SourceType: "workflow-update",
                SourceMessageId: operation.WorkflowInstanceId ?? operation.Id,
                DeduplicationKey: CreateWorkflowDeduplicationKey(operation, content)),
            cancellationToken);
    }

    private static string CreateWorkflowDeduplicationKey(AgentOperation operation, string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return $"workflow:{operation.Id}:{operation.Status}:{operation.CurrentStep}:{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private async Task<OperationOutput> PublishCapitalCallOutputAsync(
        AgentOperation operation,
        CapitalCallWorkflowCompleted completedPayload,
        CancellationToken cancellationToken)
    {
        var output = await _operationOutputRepository.GetLatestByOperationAsync(operation.Id, operation.TenantId, cancellationToken)
            ?? new OperationOutput
            {
                TenantId = operation.TenantId,
                ConversationId = operation.ConversationId,
                OperationId = operation.Id,
                SourceAgentId = operation.AgentId,
                OutputType = FundAdministrationOutputTypes.CapitalCallReviewedAllocations
            };

        output.DisplayName = string.IsNullOrWhiteSpace(completedPayload.Notice.RootFundName)
            ? "Capital call reviewed allocations"
            : $"{completedPayload.Notice.RootFundName} reviewed capital call allocations";
        output.Summary = "Approved capital call allocations ready for downstream workflows.";
        output.Status = OperationOutputStatus.Published;
        output.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var operationData = new CapitalCallOperationData
        {
            OutputId = output.Id,
            RequestState = completedPayload.ReviewedExtraction.RequestState,
            ReviewedExtraction = completedPayload.ReviewedExtraction,
            FundName = completedPayload.Notice.RootFundName,
            Notice = completedPayload.Notice,
            ReviewDownloadRoute = completedPayload.ReviewedExtraction.ReviewDownloadRoute,
            ReviewFileName = completedPayload.ReviewedExtraction.ReviewFileName
        };

        output.PayloadJson = JsonContent.Serialize(operationData);
        await _operationOutputRepository.UpsertAsync(output, cancellationToken);
        return output;
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

    private static AuditEvent BuildCapitalCallAuditEvent(AgentOperation operation, string eventType, object payload) =>
        new()
        {
            TenantId = operation.TenantId,
            ConversationId = operation.ConversationId,
            OperationId = operation.Id,
            EventType = eventType,
            ActorType = "Workflow",
            ActorId = FundAdministrationWorkflowNames.CapitalCallNotice,
            DataJson = JsonContent.Serialize(payload)
        };

    private static void CopyOperationState(AgentOperation source, AgentOperation target)
    {
        target.Title = source.Title;
        target.Status = source.Status;
        target.CurrentStep = source.CurrentStep;
        target.Summary = source.Summary;
        target.PendingClarification = source.PendingClarification;
        target.ActiveReviewTaskId = source.ActiveReviewTaskId;
        target.SourceOperationId = source.SourceOperationId;
        target.SourceOutputId = source.SourceOutputId;
        target.WorkflowInstanceId = source.WorkflowInstanceId;
        target.LatestCheckpointId = source.LatestCheckpointId;
        target.DataJson = source.DataJson;
        target.UpdatedAtUtc = source.UpdatedAtUtc;
    }

    private static bool HasWorkflowManagedCapitalCallState(AgentOperation operation) =>
        operation.Status is AgentOperationStatus.Completed or AgentOperationStatus.ClarificationRequired or AgentOperationStatus.Failed
        || !string.IsNullOrWhiteSpace(operation.WorkflowInstanceId)
        || !string.IsNullOrWhiteSpace(operation.LatestCheckpointId)
        || !string.IsNullOrWhiteSpace(operation.DataJson)
        || !string.IsNullOrWhiteSpace(operation.PendingClarification)
        || !string.IsNullOrWhiteSpace(operation.Summary)
        || !string.Equals(operation.CurrentStep, "Intake", StringComparison.OrdinalIgnoreCase);

    private static string ResolveCapitalCallClarificationPrompt(
        WorkflowRunResult result,
        WorkflowPendingRequest pendingRequest)
    {
        var workflowMessage = result.Outputs
            .LastOrDefault(output => string.Equals(output.OutputType, "MessageActivity", StringComparison.Ordinal));

        if (workflowMessage is not null)
        {
            if (workflowMessage.TryGetPayload<WorkflowActivityMessage>(out var payload) &&
                !string.IsNullOrWhiteSpace(payload?.Text))
            {
                return payload.Text;
            }
        }

        return !string.IsNullOrWhiteSpace(pendingRequest.PromptText)
            ? pendingRequest.PromptText
            : "I still need the missing capital call details before I can calculate the capital call allocations.";
    }

    private static string ResolveReviewTaskTitle(string portId) =>
        portId switch
        {
            FundOnboardingWorkflowPorts.ClassificationReview => "Classification Review",
            FundOnboardingWorkflowPorts.ExtractionReview => "Extraction Review",
            CapitalCallNoticeWorkflowPorts.ExtractionReview => "Capital Call Partner Data Review",
            CapitalCallNoticeWorkflowPorts.AllocationConfirmation => "Capital Call Allocation Confirmation",
            _ => "Workflow Review"
        };

    private static string ResolveReviewTaskType(string portId) =>
        portId switch
        {
            FundOnboardingWorkflowPorts.ClassificationReview => "ClassificationReview",
            FundOnboardingWorkflowPorts.ExtractionReview => "ExtractionReview",
            CapitalCallNoticeWorkflowPorts.ExtractionReview => "CapitalCallExtractionReview",
            CapitalCallNoticeWorkflowPorts.AllocationConfirmation => "CapitalCallAllocationConfirmation",
            _ => "WorkflowReview"
        };

    private static ReviewTaskInteractionMode ResolveReviewTaskInteractionMode(string portId) =>
        portId switch
        {
            CapitalCallNoticeWorkflowPorts.ExtractionReview => ReviewTaskInteractionMode.EditAndSubmit,
            CapitalCallNoticeWorkflowPorts.AllocationConfirmation => ReviewTaskInteractionMode.ApproveReject,
            _ => ReviewTaskInteractionMode.ApproveReject
        };

    private static string? ResolveReviewTaskInstruction(string portId) =>
        portId switch
        {
            CapitalCallNoticeWorkflowPorts.ExtractionReview => "Review the extracted partners, feeder structure, currencies, and commitment percentages. Edit the payload if anything is wrong, then submit it so the allocation engine uses your reviewed data.",
            CapitalCallNoticeWorkflowPorts.AllocationConfirmation => "Review the computed fund and investor allocation amounts. Approve when the final numbers look correct so the workflow can complete.",
            FundOnboardingWorkflowPorts.ClassificationReview => "Approve the document classification when the extracted categories look correct.",
            FundOnboardingWorkflowPorts.ExtractionReview => "Approve the extracted fund fields when the values look correct.",
            _ => null
        };

    private static string SerializeCapitalCallRequestState(AgentOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.DataJson))
        {
            return "{}";
        }

        var data = JsonContent.Deserialize<CapitalCallOperationData>(operation.DataJson);
        return data?.RequestState is null
            ? "{}"
            : JsonContent.Serialize(data.RequestState);
    }

    private sealed record WorkflowActivityMessage(string Text, string Role);
}
