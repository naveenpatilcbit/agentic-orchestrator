using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Reviews;

namespace ConversationalOrchestration.Application.Conversations;

public interface IPlanContinuationService
{
    Task<ChatInteractionResult?> TryContinueAfterReviewApprovalAsync(
        ReviewTask reviewTask,
        TenantExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed class PlanContinuationService : IPlanContinuationService
{
    private readonly IConversationPlanService _planService;
    private readonly IConversationRepository _conversationRepository;
    private readonly IAgentOperationRepository _operationRepository;
    private readonly IReviewTaskRepository _reviewTaskRepository;
    private readonly IFileAssetRepository _fileAssetRepository;
    private readonly IConversationTranscriptService _transcriptService;
    private readonly IConversationMessageRepository _messageRepository;
    private readonly IConversationHistoryCompactionService _compactionService;
    private readonly IMasterOrchestrationAgent _masterAgent;

    public PlanContinuationService(
        IConversationPlanService planService,
        IConversationRepository conversationRepository,
        IAgentOperationRepository operationRepository,
        IReviewTaskRepository reviewTaskRepository,
        IFileAssetRepository fileAssetRepository,
        IConversationTranscriptService transcriptService,
        IConversationMessageRepository messageRepository,
        IConversationHistoryCompactionService compactionService,
        IMasterOrchestrationAgent masterAgent)
    {
        _planService = planService;
        _conversationRepository = conversationRepository;
        _operationRepository = operationRepository;
        _reviewTaskRepository = reviewTaskRepository;
        _fileAssetRepository = fileAssetRepository;
        _transcriptService = transcriptService;
        _messageRepository = messageRepository;
        _compactionService = compactionService;
        _masterAgent = masterAgent;
    }

    public async Task<ChatInteractionResult?> TryContinueAfterReviewApprovalAsync(
        ReviewTask reviewTask,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversationRepository.GetAsync(reviewTask.ConversationId, context.TenantId, cancellationToken);
        if (conversation is null)
        {
            return null;
        }

        var plan = await _planService.GetActiveAsync(conversation.Id, context, cancellationToken);
        if (plan is null || plan.Status != ConversationPlanStatus.Active)
        {
            return null;
        }

        if (plan.NextStepIndex >= plan.Steps.Count)
        {
            return null;
        }

        var nextStepText = plan.Steps[plan.NextStepIndex].Text;
        var syntheticText =
            $"The previous review was approved. Continue the saved multi-step plan now. Next step ({plan.NextStepIndex + 1}/{plan.Steps.Count}): {nextStepText}";

        var triggerMessage = await _transcriptService.AppendAsync(
            new ConversationTranscriptAppendRequest(
                context.TenantId,
                conversation.Id,
                "system",
                ConversationMessageRole.User,
                syntheticText,
                MessageKind: "plan-continuation",
                OperationId: reviewTask.OperationId,
                SourceType: "plan-continuation",
                SourceMessageId: reviewTask.Id,
                DeduplicationKey: $"plan-continuation:{reviewTask.Id}"),
            cancellationToken);

        // Ensure history compaction includes this event.
        await _messageRepository.UpsertAsync(triggerMessage, cancellationToken);
        await _compactionService.RefreshAsync(context.TenantId, conversation.Id, cancellationToken);

        var operations = await _operationRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);
        var reviewTasks = await _reviewTaskRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);
        var files = await _fileAssetRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);

        var result = await _masterAgent.RunAsync(
            new MasterOrchestrationRequest(
                syntheticText,
                conversation,
                triggerMessage,
                operations,
                reviewTasks,
                files,
                context),
            cancellationToken);

        // Advance plan pointer optimistically by one. If the next step still blocks on prerequisites,
        // it will be re-attempted on the next approval event.
        await _planService.AdvanceAsync(conversation.Id, context, plan.NextStepIndex + 1, cancellationToken);

        return new ChatInteractionResult(
            result.AssistantMessage,
            conversation.Id,
            result.OperationId,
            result.Actions);
    }
}

