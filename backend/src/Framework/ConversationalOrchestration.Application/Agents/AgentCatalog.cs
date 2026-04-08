using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Domain.Agents;

namespace ConversationalOrchestration.Application.Agents;

public sealed class AgentCatalog : IAgentCatalog
{
    private readonly IReadOnlyDictionary<string, IAgent> _agents;

    public AgentCatalog(IEnumerable<IAgent> agents)
    {
        _agents = agents.ToDictionary(agent => agent.Definition.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<AgentDefinition> List() =>
        _agents.Values.Select(static agent => agent.Definition).ToArray();

    public IAgent Resolve(string agentId) =>
        _agents.TryGetValue(agentId, out var agent)
            ? agent
            : throw new InvalidOperationException($"Unknown agent '{agentId}'.");
}
