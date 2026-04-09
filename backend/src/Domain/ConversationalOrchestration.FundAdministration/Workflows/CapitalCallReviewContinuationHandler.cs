using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using ConversationalOrchestration.FundAdministration.Agents;

namespace ConversationalOrchestration.FundAdministration.Workflows;

public sealed class CapitalCallReviewContinuationHandler : IReviewContinuationHandler
{
    private readonly IFundAdministrationWorkflowDispatcher _workflowDispatcher;

    public CapitalCallReviewContinuationHandler(IFundAdministrationWorkflowDispatcher workflowDispatcher)
    {
        _workflowDispatcher = workflowDispatcher;
    }

    public bool CanHandle(AgentOperation operation, ReviewTask reviewTask) =>
        operation.AgentId == FundAdministrationAgentIds.NoticeCreation &&
        string.Equals(reviewTask.TaskType, "CapitalCallExtractionReview", StringComparison.Ordinal);

    public Task HandleApprovedAsync(
        ReviewContinuationContext context,
        CancellationToken cancellationToken) =>
        _workflowDispatcher.ContinueCapitalCallReviewAsync(
            context.Operation,
            context.Conversation,
            context.ReviewTask.Id,
            context.ReviewTask.FinalPayloadJson ?? context.ReviewTask.ProposedPayloadJson,
            context.RequestContext,
            cancellationToken);
}
