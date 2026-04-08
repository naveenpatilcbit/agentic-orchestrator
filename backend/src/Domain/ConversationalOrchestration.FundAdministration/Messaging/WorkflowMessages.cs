using NServiceBus;

namespace ConversationalOrchestration.FundAdministration.Messaging;

public static class FundAdministrationEndpointNames
{
    public const string Api = "conversational-orchestration-api";
    public const string Workflow = "conversational-orchestration-fund-admin-workflow";
    public const string Error = "conversational-orchestration-error";
    public const string Audit = "conversational-orchestration-audit";
}

public sealed class StartFundOnboardingCommand : ICommand
{
    public string OperationId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public List<string> AttachmentIds { get; set; } = [];
}

public sealed class ContinueFundOnboardingReviewCommand : ICommand
{
    public string OperationId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ReviewTaskId { get; set; } = string.Empty;
    public string ReviewType { get; set; } = string.Empty;
    public string FinalPayloadJson { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
}
