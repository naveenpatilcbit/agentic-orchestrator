namespace ConversationalOrchestration.Domain.Agents;

public enum AgentExecutionMode
{
    InlineFunction = 1,
    SagaWorkflow = 2
}

public sealed record AgentDefinition(
    string Id,
    string DisplayName,
    string Description,
    AgentExecutionMode ExecutionMode);
