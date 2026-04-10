using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
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
            var messages =
                new[]
                {
                    new ChatMessage(ChatRole.System, request.SystemPrompt),
                    new ChatMessage(ChatRole.User, request.UserPrompt)
                };

            var structuredResult = await TryGetStructuredResponseAsync<TResponse>(
                client,
                messages,
                useJsonSchemaResponseFormat: true,
                cancellationToken);

            if (structuredResult is not null)
            {
                return structuredResult;
            }

            _logger.LogWarning(
                "Schema-based structured output did not yield a usable result for profile {Profile}. Falling back to non-schema JSON mode.",
                request.Profile);

            return await TryGetStructuredResponseAsync<TResponse>(
                client,
                messages,
                useJsonSchemaResponseFormat: false,
                cancellationToken);
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
            LlmProfile.ReviewRevision => string.IsNullOrWhiteSpace(options.InputCompletionModel)
                ? options.RoutingModel
                : options.InputCompletionModel,
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported LLM profile.")
        };

    private async Task<TResponse?> TryGetStructuredResponseAsync<TResponse>(
        IChatClient client,
        IReadOnlyCollection<ChatMessage> messages,
        bool useJsonSchemaResponseFormat,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        try
        {
            var response = await client.GetResponseAsync<TResponse>(
                messages,
                JsonOptions,
                options: null,
                useJsonSchemaResponseFormat: useJsonSchemaResponseFormat,
                cancellationToken: cancellationToken);

            if (response.TryGetResult(out var result) && result is not null)
            {
                return result;
            }

            _logger.LogWarning(
                "Structured output response could not be parsed for schema mode {UseJsonSchemaResponseFormat}. Response preview: {ResponsePreview}",
                useJsonSchemaResponseFormat,
                Truncate(response.Text));
            return null;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Structured output response failed deserialization for schema mode {UseJsonSchemaResponseFormat}.",
                useJsonSchemaResponseFormat);
            return null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Structured output request failed for schema mode {UseJsonSchemaResponseFormat}.",
                useJsonSchemaResponseFormat);
            return null;
        }
    }

    private static string Truncate(string? responseText) =>
        string.IsNullOrWhiteSpace(responseText)
            ? "(empty)"
            : responseText.Length <= 500
                ? responseText
                : responseText[..500];
}
