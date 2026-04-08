namespace ConversationalOrchestration.Infrastructure.Configuration;

public sealed class LlmGatewayOptions
{
    public const string SectionName = "LlmGateway";

    public string Provider { get; init; } = "OpenAICompatible";
    public string ApiKey { get; init; } = string.Empty;
    public string? BaseUrl { get; init; }
    public int TimeoutSeconds { get; init; } = 20;
    public string RoutingModel { get; init; } = "gpt-4.1-mini";
    public string InputCompletionModel { get; init; } = "gpt-4.1-mini";
}
