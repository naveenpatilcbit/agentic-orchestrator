using System.Text.Json;
using System.Text.Json.Serialization;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Infrastructure.Conversations;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ConversationalOrchestration.Infrastructure.AI;

/// <summary>
/// Option A: the application writes the transcript to Mongo; the Agent Framework uses a read-only
/// <see cref="MongoConversationChatHistoryProvider"/> to load history for each run.
/// </summary>
public sealed class MultiTurnStructuredAgentClient : IMultiTurnStructuredAgentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILlmChatClientFactory _chatClientFactory;
    private readonly MongoConversationChatHistoryProvider _chatHistoryProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MultiTurnStructuredAgentClient> _logger;

    public MultiTurnStructuredAgentClient(
        ILlmChatClientFactory chatClientFactory,
        MongoConversationChatHistoryProvider chatHistoryProvider,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        ILogger<MultiTurnStructuredAgentClient> logger)
    {
        _chatClientFactory = chatClientFactory;
        _chatHistoryProvider = chatHistoryProvider;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<TResponse?> GetAsync<TResponse>(
        MultiTurnStructuredAgentRequest request,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var chatClient = _chatClientFactory.TryGetChatClient(request.Profile);
        if (chatClient is null)
        {
            _logger.LogWarning(
                "Multi-turn structured agent call skipped because no chat client is configured. Profile={Profile} AgentId={AgentId}",
                request.Profile,
                request.AgentId);
            return null;
        }

        var agentOptions = new ChatClientAgentOptions
        {
            Id = request.AgentId,
            Name = request.AgentName,
            Description = request.AgentDescription,
            ChatOptions = request.ChatOptions,
            ChatHistoryProvider = _chatHistoryProvider
        };

        var agent = new ChatClientAgent(
            chatClient,
            agentOptions,
            _loggerFactory,
            _serviceProvider);

        // For Option A, we rehydrate history from Mongo on each run (via the history provider).
        // The transcript remains application-owned; the framework session is ephemeral here.
        //
        // Microsoft.Agents.AI 1.0.0 keeps AgentSession constructors non-public, so we have to
        // instantiate a concrete session via reflection.
        var session = CreateSession();
        _chatHistoryProvider.BindSession(
            session,
            request.TenantId,
            request.ConversationId,
            request.OperationId);

        var messages = new[]
        {
            new ChatMessage(ChatRole.System, request.SystemPrompt),
            new ChatMessage(ChatRole.User, request.UserPrompt)
        };

        try
        {
            var response = await agent.RunAsync<TResponse>(
                messages,
                session,
                JsonOptions,
                options: null,
                cancellationToken: cancellationToken);

            // AgentResponse<T> shape isn't documented in the XML for 1.0.0, so keep the
            // access pattern tolerant.
            if (response is null)
            {
                return null;
            }

            // Most common: response.Value / response.Result / response.Output
            var typed = TryExtractTypedResponse<TResponse>(response);
            if (typed is not null)
            {
                return typed;
            }

            _logger.LogWarning(
                "Multi-turn structured agent response did not expose a typed payload. AgentId={AgentId} Profile={Profile}",
                request.AgentId,
                request.Profile);
            return null;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Multi-turn structured agent failed JSON deserialization. AgentId={AgentId} Profile={Profile}",
                request.AgentId,
                request.Profile);
            return null;
        }
    }

    private static TResponse? TryExtractTypedResponse<TResponse>(object response)
        where TResponse : class
    {
        var type = response.GetType();
        foreach (var propertyName in new[] { "Value", "Result", "Output", "Response", "Data" })
        {
            var property = type.GetProperty(propertyName);
            if (property is null)
            {
                continue;
            }

            if (property.GetValue(response) is TResponse typed)
            {
                return typed;
            }
        }

        return null;
    }

    private static AgentSession CreateSession()
    {
        var session = Activator.CreateInstance(typeof(ChatClientAgentSession), nonPublic: true) as AgentSession;
        if (session is null)
        {
            throw new InvalidOperationException("Failed to create AgentSession for ChatClientAgent runs.");
        }

        return session;
    }
}
