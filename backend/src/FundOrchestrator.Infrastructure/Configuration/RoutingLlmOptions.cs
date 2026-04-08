namespace FundOrchestrator.Infrastructure.Configuration;

public sealed class RoutingLlmOptions
{
    public const string SectionName = "RoutingLlm";

    public string BaseUrl { get; set; } = "https://api.openai.com";
    public string EndpointPath { get; set; } = "/v1/chat/completions";
    public string Model { get; set; } = "gpt-4.1-mini";
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 20;
}
