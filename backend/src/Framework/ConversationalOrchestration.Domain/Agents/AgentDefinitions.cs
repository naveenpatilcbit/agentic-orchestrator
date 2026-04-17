namespace ConversationalOrchestration.Domain.Agents;

public sealed record AgentInputFieldDefinition(
    string Name,
    string Label,
    string Description,
    bool Required,
    string? Example = null);

public sealed record AgentStartRequirements(
    IReadOnlyCollection<AgentInputFieldDefinition> Fields,
    bool AllowsPartialStart = true,
    bool RequiresAttachment = false,
    string? Guidance = null);

public sealed record AgentSourceRequirements(
    IReadOnlyCollection<string> AcceptedOutputTypes,
    bool RequiresSource = false,
    string? Guidance = null);

public enum AgentExecutionMode
{
    InlineFunction = 1,
    Workflow = 2
}

public sealed record AgentDefinition(
    string Id,
    string DisplayName,
    string Description,
    AgentExecutionMode ExecutionMode,
    AgentStartRequirements? StartRequirements = null,
    AgentSourceRequirements? SourceRequirements = null);
