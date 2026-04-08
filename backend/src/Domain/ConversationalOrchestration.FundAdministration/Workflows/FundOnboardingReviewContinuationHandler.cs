using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using ConversationalOrchestration.FundAdministration.Agents;

namespace ConversationalOrchestration.FundAdministration.Workflows;

public sealed class FundOnboardingReviewContinuationHandler : IReviewContinuationHandler
{
    private readonly IFundAdministrationWorkflowDispatcher _workflowDispatcher;

    public FundOnboardingReviewContinuationHandler(IFundAdministrationWorkflowDispatcher workflowDispatcher)
    {
        _workflowDispatcher = workflowDispatcher;
    }

    public bool CanHandle(AgentOperation operation, ReviewTask reviewTask) =>
        operation.AgentId == FundAdministrationAgentIds.FundOnboarding;

    public Task HandleApprovedAsync(
        ReviewContinuationContext context,
        CancellationToken cancellationToken) =>
        _workflowDispatcher.ContinueOnboardingReviewAsync(
            context.Operation,
            context.Conversation,
            context.ReviewTask.Id,
            context.ReviewTask.TaskType,
            context.ReviewTask.FinalPayloadJson ?? context.ReviewTask.ProposedPayloadJson,
            context.RequestContext,
            cancellationToken);
}
