using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Infrastructure.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using System.ClientModel;

namespace ConversationalOrchestration.Infrastructure.AI;

public sealed class MicrosoftExtensionsAiStructuredLlmClient : IStructuredLlmClient, ILlmChatClientFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<LlmProfile, IChatClient> _chatClients = new();
    private readonly IOptions<LlmGatewayOptions> _options;
    private readonly ILogger<MicrosoftExtensionsAiStructuredLlmClient> _logger;

    public MicrosoftExtensionsAiStructuredLlmClient(
        IOptions<LlmGatewayOptions> options,
        ILogger<MicrosoftExtensionsAiStructuredLlmClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<TResponse?> GetStructuredResponseAsync<TResponse>(
        StructuredLlmRequest request,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var options = _options.Value;
        var model = ResolveModel(options, request.Profile);
        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(model))
        {
            _logger.LogWarning("LLM gateway is not configured for profile {Profile}.", request.Profile);
            return null;
        }

        try
        {
            var client = _chatClients.GetOrAdd(request.Profile, _ => CreateChatClient(options, model));
            var response = await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, request.SystemPrompt),
                    new ChatMessage(ChatRole.User, request.UserPrompt)
                ],
                new ChatOptions
                {
                    ResponseFormat = ChatResponseFormat.Json
                },
                cancellationToken);

            if (string.IsNullOrWhiteSpace(response.Text))
            {
                _logger.LogWarning("LLM gateway returned an empty response for profile {Profile}.", request.Profile);
                return null;
            }

            return JsonSerializer.Deserialize<TResponse>(response.Text, JsonOptions);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "LLM gateway call failed for profile {Profile}.", request.Profile);
            return null;
        }
    }

    public IChatClient? TryGetChatClient(LlmProfile profile)
    {
        var options = _options.Value;
        var model = ResolveModel(options, profile);
        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return _chatClients.GetOrAdd(profile, _ => CreateChatClient(options, model));
    }

    private IChatClient CreateChatClient(LlmGatewayOptions options, string model)
    {
        if (!options.Provider.Equals("OpenAICompatible", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Provider '{options.Provider}' is not supported by the current infrastructure adapter.");
        }

        OpenAIClient client = string.IsNullOrWhiteSpace(options.BaseUrl)
            ? new OpenAIClient(new ApiKeyCredential(options.ApiKey))
            : new OpenAIClient(
                credential: new ApiKeyCredential(options.ApiKey),
                options: new OpenAIClientOptions
                {
                    Endpoint = new Uri(options.BaseUrl, UriKind.Absolute)
                });

        return client.GetChatClient(model).AsIChatClient();
    }

    private static string ResolveModel(LlmGatewayOptions options, LlmProfile profile) =>
        profile switch
        {
            LlmProfile.Routing => options.RoutingModel,
            LlmProfile.InputCompletion => options.InputCompletionModel,
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported LLM profile.")
        };
}
