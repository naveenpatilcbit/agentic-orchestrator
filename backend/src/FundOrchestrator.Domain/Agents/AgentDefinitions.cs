namespace FundOrchestrator.Domain.Agents;

public enum AgentExecutionMode
{
    InlineFunction = 1,
    SagaWorkflow = 2
}

public static class AgentIds
{
    public const string NoticeCreation = "notice-creation";
    public const string FundOnboarding = "fund-onboarding";
    public const string OnePager = "one-pager";
}

public sealed record AgentDefinition(
    string Id,
    string DisplayName,
    string Description,
    AgentExecutionMode ExecutionMode,
    IReadOnlyCollection<string> Keywords);
