namespace ConversationalOrchestration.Application.Abstractions;

public enum LlmProfile
{
    Routing = 1,
    InputCompletion = 2
}

public sealed record StructuredLlmRequest(
    LlmProfile Profile,
    string SystemPrompt,
    string UserPrompt);

public interface IStructuredLlmClient
{
    Task<TResponse?> GetStructuredResponseAsync<TResponse>(
        StructuredLlmRequest request,
        CancellationToken cancellationToken)
        where TResponse : class;
}
