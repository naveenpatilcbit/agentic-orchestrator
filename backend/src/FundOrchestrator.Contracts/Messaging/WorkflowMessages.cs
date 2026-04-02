using NServiceBus;

namespace FundOrchestrator.Contracts.Messaging;

public static class EndpointNames
{
    public const string Api = "fund-orchestrator-api";
    public const string Workflow = "fund-orchestrator-workflow";
}

public sealed class StartOnboardingWorkflowCommand : ICommand
{
    public string OperationId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public List<string> AttachmentIds { get; set; } = [];
}

public sealed class AdvanceOnboardingReviewCommand : ICommand
{
    public string OperationId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ReviewTaskId { get; set; } = string.Empty;
    public string ReviewType { get; set; } = string.Empty;
    public string FinalPayloadJson { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
}
