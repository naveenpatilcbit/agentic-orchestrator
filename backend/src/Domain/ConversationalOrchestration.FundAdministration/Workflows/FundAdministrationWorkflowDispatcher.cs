using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.FundAdministration.Messaging;
using NServiceBus;

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
    private readonly IMessageSession _messageSession;

    public FundAdministrationWorkflowDispatcher(IMessageSession messageSession)
    {
        _messageSession = messageSession;
    }

    public Task StartOnboardingAsync(
        AgentOperation operation,
        ConversationThread conversation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken) =>
        _messageSession.Send(
            new StartFundOnboardingCommand
            {
                OperationId = operation.Id,
                ConversationId = conversation.Id,
                TenantId = context.TenantId,
                UserId = context.UserId,
                AttachmentIds = attachments.Select(asset => asset.Id).ToList()
            },
            cancellationToken);

    public Task ContinueOnboardingReviewAsync(
        AgentOperation operation,
        ConversationThread conversation,
        string reviewTaskId,
        string reviewType,
        string finalPayloadJson,
        TenantExecutionContext context,
        CancellationToken cancellationToken) =>
        _messageSession.Send(
            new ContinueFundOnboardingReviewCommand
            {
                OperationId = operation.Id,
                ConversationId = conversation.Id,
                TenantId = context.TenantId,
                ReviewTaskId = reviewTaskId,
                ReviewType = reviewType,
                FinalPayloadJson = finalPayloadJson,
                UserId = context.UserId
            },
            cancellationToken);
}
