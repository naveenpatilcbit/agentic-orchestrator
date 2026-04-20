using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;

namespace ConversationalOrchestration.Application.Abstractions;

public interface IMasterOrchestrationAgent
{
    Task<MasterOrchestrationResult> RunAsync(
        MasterOrchestrationRequest request,
        CancellationToken cancellationToken);
}

public sealed record MasterOrchestrationRequest(
    string Message,
    ConversationThread Conversation,
    ConversationMessage UserMessage,
    IReadOnlyCollection<AgentOperation> Operations,
    IReadOnlyCollection<ReviewTask> ReviewTasks,
    IReadOnlyCollection<FileAsset> Attachments,
    TenantExecutionContext Context);

public sealed record MasterOrchestrationResult(
    string AssistantMessage,
    string? OperationId = null,
    IReadOnlyCollection<AgentAction>? Actions = null);
