using ConversationalOrchestration.Application.Abstractions;
using Microsoft.Extensions.AI;

namespace ConversationalOrchestration.Infrastructure.AI;

public interface ILlmChatClientFactory
{
    IChatClient? TryGetChatClient(LlmProfile profile);
}

