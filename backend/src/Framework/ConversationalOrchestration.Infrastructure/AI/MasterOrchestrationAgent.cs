using System.Text.Json;
using System.Text.Json.Serialization;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Conversations;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using ConversationalOrchestration.Infrastructure.Conversations;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace ConversationalOrchestration.Infrastructure.AI;

/// <summary>
/// Master agent that can invoke existing workflow agents as tools.
/// This keeps orchestration autonomous without introducing an additional orchestration backend.
/// </summary>
public sealed class MasterOrchestrationAgent : IMasterOrchestrationAgent
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Lazy<Func<AgentSession>> SessionFactory = new(MultiTurnStructuredAgentClientAccessor.CreateSessionFactory);

    private readonly ILlmChatClientFactory _chatClientFactory;
    private readonly MongoConversationChatHistoryProvider _chatHistoryProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IAgentCatalog _agentCatalog;
    private readonly IConversationRepository _conversationRepository;
    private readonly IConversationMessageRepository _conversationMessageRepository;
    private readonly IConversationTranscriptService _conversationTranscriptService;
    private readonly IConversationHistoryCompactionService _conversationHistoryCompactionService;
    private readonly IAgentOperationRepository _operationRepository;
    private readonly ILogger<MasterOrchestrationAgent> _logger;

    public MasterOrchestrationAgent(
        ILlmChatClientFactory chatClientFactory,
        MongoConversationChatHistoryProvider chatHistoryProvider,
        ILoggerFactory loggerFactory,
        IAgentCatalog agentCatalog,
        IConversationRepository conversationRepository,
        IConversationMessageRepository conversationMessageRepository,
        IConversationTranscriptService conversationTranscriptService,
        IConversationHistoryCompactionService conversationHistoryCompactionService,
        IAgentOperationRepository operationRepository,
        ILogger<MasterOrchestrationAgent> logger)
    {
        _chatClientFactory = chatClientFactory;
        _chatHistoryProvider = chatHistoryProvider;
        _loggerFactory = loggerFactory;
        _agentCatalog = agentCatalog;
        _conversationRepository = conversationRepository;
        _conversationMessageRepository = conversationMessageRepository;
        _conversationTranscriptService = conversationTranscriptService;
        _conversationHistoryCompactionService = conversationHistoryCompactionService;
        _operationRepository = operationRepository;
        _logger = logger;
    }

    public async Task<MasterOrchestrationResult> RunAsync(
        MasterOrchestrationRequest request,
        CancellationToken cancellationToken)
    {
        var chatClient = _chatClientFactory.TryGetChatClient(LlmProfile.Routing);
        if (chatClient is null)
        {
            _logger.LogWarning(
                "Master orchestration call skipped because no chat client is configured. TenantId={TenantId} ConversationId={ConversationId}",
                request.Context.TenantId,
                request.Conversation.Id);

            return new MasterOrchestrationResult(
                "I can’t run the master agent right now because the LLM gateway isn’t configured.");
        }

        var runState = new MasterAgentRunState(request, _agentCatalog);
        var tools = BuildTools(runState);

        var agentOptions = new ChatClientAgentOptions
        {
            Id = "master-agent",
            Name = "Master agent",
            Description = "Autonomous orchestrator that selects and runs workflow agents as tools.",
            ChatHistoryProvider = _chatHistoryProvider,
            ChatOptions = new ChatOptions
            {
                Tools = tools,
                ToolMode = ChatToolMode.Auto
            }
        };

        var agent = new ChatClientAgent(
            chatClient,
            agentOptions,
            _loggerFactory,
            services: null);

        var session = SessionFactory.Value();
        _chatHistoryProvider.BindSession(session, request.Context.TenantId, request.Conversation.Id, operationId: null);

        var messages = new[]
        {
            new ChatMessage(ChatRole.System, BuildSystemPrompt()),
            new ChatMessage(ChatRole.User, BuildUserPrompt(request, _agentCatalog))
        };

        AgentResponse? response;
        try
        {
            response = await agent.RunAsync(messages, session, options: null, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Master agent run failed. TenantId={TenantId} ConversationId={ConversationId}",
                request.Context.TenantId,
                request.Conversation.Id);

            return new MasterOrchestrationResult("I couldn’t complete that request due to an internal error.");
        }

        var assistantText = string.IsNullOrWhiteSpace(response?.Text)
            ? runState.LastResult?.AssistantMessage ?? "I couldn't produce a response."
            : response!.Text.Trim();

        return new MasterOrchestrationResult(
            assistantText,
            runState.LastResult?.Operation.Id ?? request.Conversation.ActiveOperationId,
            runState.LastResult?.Actions);
    }

    private IList<AITool> BuildTools(MasterAgentRunState state)
    {
        // Tool methods capture state and operate on framework repositories. These are side-effecting.
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                (string agentId, CancellationToken ct) => StartAgentOperationAsync(agentId, state, ct),
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = JsonOptions,
                    Name = "start_agent_operation",
                    Description = "Start a new operation for the specified agent id in the current conversation."
                }),

            AIFunctionFactory.Create(
                (CancellationToken ct) => ContinueActiveOperationAsync(state, ct),
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = JsonOptions,
                    Name = "continue_active_operation",
                    Description = "Continue the currently active operation in the conversation using the user's latest message."
                }),

            AIFunctionFactory.Create(
                (string? operationId, CancellationToken ct) => DescribeStatusAsync(operationId, state, ct),
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = JsonOptions,
                    Name = "describe_status",
                    Description = "Get a concise status update for an operation (or the active operation if omitted)."
                })
        };

        // Convenience: expose every registered agent as its own start tool.
        // This reduces tool-call argument errors and makes chaining easier for the LLM.
        foreach (var agent in state.Catalog.List())
        {
            var toolName = $"start_{SanitizeFunctionName(agent.Id)}";
            tools.Add(AIFunctionFactory.Create(
                (CancellationToken ct) => StartAgentOperationAsync(agent.Id, state, ct),
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = JsonOptions,
                    Name = toolName,
                    Description = $"Start: {agent.DisplayName}. {agent.Description}"
                }));
        }

        return tools;
    }

    private static string SanitizeFunctionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unknown";
        }

        Span<char> buffer = stackalloc char[Math.Min(64, name.Length)];
        var written = 0;

        foreach (var ch in name.Trim())
        {
            if (written >= buffer.Length)
            {
                break;
            }

            buffer[written++] = char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_';
        }

        return written == 0 ? "unknown" : new string(buffer[..written]);
    }

    private async Task<string> StartAgentOperationAsync(
        string agentId,
        MasterAgentRunState state,
        CancellationToken cancellationToken)
    {
        var request = state.Request;
        var conversation = request.Conversation;

        var operations = await _operationRepository.ListByConversationAsync(conversation.Id, request.Context.TenantId, cancellationToken);
        var activeOperation = ConversationSessionStateResolver.ResolveActiveOperation(conversation, operations);
        if (activeOperation is not null)
        {
            return $"This conversation already has active work: {activeOperation.Title}. Continue it here, or start a new conversation for separate work.";
        }

        if (string.IsNullOrWhiteSpace(agentId))
        {
            return "I need an agent id to start new work.";
        }

        var agent = _agentCatalog.Resolve(agentId);

        var operation = new AgentOperation
        {
            TenantId = request.Context.TenantId,
            ConversationId = conversation.Id,
            AgentId = agent.Definition.Id,
            Title = agent.Definition.DisplayName,
            CreatedByUserId = request.Context.UserId,
            Status = AgentOperationStatus.Received
        };

        await _operationRepository.UpsertAsync(operation, cancellationToken);

        conversation.ActiveOperationId = operation.Id;
        conversation.LastFocusedOperationId = operation.Id;
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _conversationRepository.UpsertAsync(conversation, cancellationToken);

        // Bind the user message to this operation so operation-scoped history compaction works.
        request.UserMessage.OperationId = operation.Id;
        await _conversationMessageRepository.UpsertAsync(request.UserMessage, cancellationToken);
        await _conversationHistoryCompactionService.RefreshAsync(request.Context.TenantId, conversation.Id, cancellationToken);

        // Load recent history (operation scoped) for the agent.
        var history = await _conversationTranscriptService.ListByConversationAsync(conversation.Id, request.Context.TenantId, cancellationToken);

        AgentExecutionResult result;
        try
        {
            operation.Status = AgentOperationStatus.Running;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _operationRepository.UpsertAsync(operation, cancellationToken);

            result = await agent.StartAsync(
                conversation,
                history,
                request.UserMessage,
                operation,
                request.Attachments,
                request.Context,
                cancellationToken);
        }
        catch (Exception exception)
        {
            operation.Status = AgentOperationStatus.Failed;
            operation.CurrentStep = "StartFailed";
            operation.PendingClarification = "The operation could not be started. Retry the request once the issue is addressed.";
            operation.Summary = exception.Message;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _operationRepository.UpsertAsync(operation, cancellationToken);

            return $"I couldn't start {agent.Definition.DisplayName} yet: {exception.Message}";
        }

        await PersistOperationStateAsync(conversation, result.Operation, request.Context, cancellationToken);
        state.SetLastResult(result);
        return result.AssistantMessage;
    }

    private async Task<string> ContinueActiveOperationAsync(
        MasterAgentRunState state,
        CancellationToken cancellationToken)
    {
        var request = state.Request;
        var conversation = request.Conversation;

        var operations = await _operationRepository.ListByConversationAsync(conversation.Id, request.Context.TenantId, cancellationToken);
        var activeOperation = ConversationSessionStateResolver.ResolveActiveOperation(conversation, operations);
        if (activeOperation is null)
        {
            return "There is no active workflow in this conversation right now. Tell me what you want to do, and I’ll start it.";
        }

        var agent = _agentCatalog.Resolve(activeOperation.AgentId);

        // Bind message -> operation if not already.
        request.UserMessage.OperationId = activeOperation.Id;
        await _conversationMessageRepository.UpsertAsync(request.UserMessage, cancellationToken);
        await _conversationHistoryCompactionService.RefreshAsync(request.Context.TenantId, conversation.Id, cancellationToken);

        var history = await _conversationTranscriptService.ListByConversationAsync(conversation.Id, request.Context.TenantId, cancellationToken);

        AgentExecutionResult result;
        try
        {
            result = await agent.ContinueAsync(
                conversation,
                history,
                request.UserMessage,
                activeOperation,
                request.Attachments,
                request.Context,
                cancellationToken);
        }
        catch (Exception exception)
        {
            activeOperation.Status = AgentOperationStatus.Failed;
            activeOperation.CurrentStep = "ContinueFailed";
            activeOperation.Summary = exception.Message;
            activeOperation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _operationRepository.UpsertAsync(activeOperation, cancellationToken);

            return $"I couldn't continue {agent.Definition.DisplayName}: {exception.Message}";
        }

        await PersistOperationStateAsync(conversation, result.Operation, request.Context, cancellationToken);
        state.SetLastResult(result);
        return result.AssistantMessage;
    }

    private async Task<string> DescribeStatusAsync(
        string? operationId,
        MasterAgentRunState state,
        CancellationToken cancellationToken)
    {
        var request = state.Request;
        var conversation = request.Conversation;
        var operations = request.Operations;
        var reviewTasks = request.ReviewTasks;

        AgentOperation? resolved = null;
        if (!string.IsNullOrWhiteSpace(operationId))
        {
            resolved = operations.FirstOrDefault(candidate => candidate.Id == operationId);
        }

        resolved ??= ConversationSessionStateResolver.ResolveActiveOperation(conversation, operations);
        if (resolved is null)
        {
            return "There is no active workflow in this conversation right now.";
        }

        var agent = _agentCatalog.Resolve(resolved.AgentId);
        return await agent.DescribeStatusAsync(
            resolved,
            reviewTasks.Where(task => task.OperationId == resolved.Id).ToArray(),
            cancellationToken);
    }

    private async Task PersistOperationStateAsync(
        ConversationThread conversation,
        AgentOperation operation,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _operationRepository.UpsertAsync(operation, cancellationToken);

        var nextActive = ConversationSessionStateResolver.IsActiveOperation(operation) ? operation : null;
        ConversationSessionStateResolver.SyncConversationPointers(conversation, nextActive);
        conversation.LastFocusedOperationId = operation.Id;
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _conversationRepository.UpsertAsync(conversation, cancellationToken);
    }

    private static string BuildSystemPrompt() =>
        """
        You are the Master agent for an enterprise workflow automation chat app.

        You can execute work by calling tools.
        - Use tools to start or continue workflows.
        - Prefer continuing the active operation if the user is responding to the current task.
        - Only start a new operation when there is no active operation in this conversation.

        Output rules:
        - After tool calls, respond with one concise user-facing message.
        - Do not mention internal tool names, ids, or implementation details unless the user asked.
        - Do not produce multiple messages. One response only.
        """;

    private static string BuildUserPrompt(MasterOrchestrationRequest request, IAgentCatalog agentCatalog)
    {
        // Always include catalog agents; used to decide which tool to call.
        // (The tool accepts agentId; this list gives the LLM the valid ids.)
        // Note: We keep this payload compact; full conversation history is loaded via the history provider.
        var payload = new
        {
            message = request.Message,
            conversation = new
            {
                request.Conversation.Id,
                request.Conversation.Title,
                request.Conversation.ActiveOperationId,
                request.Conversation.LastFocusedOperationId
            },
            operations = request.Operations
                .OrderByDescending(item => item.UpdatedAtUtc)
                .Take(6)
                .Select(operation => new
                {
                    operation.Id,
                    operation.AgentId,
                    operation.Title,
                    Status = operation.Status.ToString(),
                    operation.CurrentStep,
                    operation.PendingClarification,
                    operation.ActiveReviewTaskId,
                    operation.UpdatedAtUtc
                }),
            reviewTasks = request.ReviewTasks
                .OrderByDescending(item => item.UpdatedAtUtc)
                .Take(6)
                .Select(task => new
                {
                    task.Id,
                    task.OperationId,
                    task.Title,
                    task.TaskType,
                    Status = task.Status.ToString(),
                    task.InteractionMode,
                    task.UpdatedAtUtc
                }),
            attachments = request.Attachments.Select(file => new
            {
                file.Id,
                file.FileName,
                file.ContentType
            }),
            availableAgents = agentCatalog.List().Select(agent => new
            {
                agent.Id,
                agent.DisplayName,
                agent.Description,
                ExecutionMode = agent.ExecutionMode.ToString(),
                StartRequirements = agent.StartRequirements is null
                    ? null
                    : new
                    {
                        agent.StartRequirements.AllowsPartialStart,
                        agent.StartRequirements.RequiresAttachment,
                        agent.StartRequirements.Guidance,
                        Fields = agent.StartRequirements.Fields.Select(field => new
                        {
                            field.Name,
                            field.Label,
                            field.Description,
                            field.Required,
                            field.Example
                        })
                    },
                SourceRequirements = agent.SourceRequirements is null
                    ? null
                    : new
                    {
                        agent.SourceRequirements.RequiresSource,
                        agent.SourceRequirements.Guidance,
                        agent.SourceRequirements.AcceptedOutputTypes
                    }
            })
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private sealed class MasterAgentRunState
    {
        public MasterAgentRunState(MasterOrchestrationRequest request, IAgentCatalog catalog)
        {
            Request = request;
            Catalog = catalog;
        }

        public MasterOrchestrationRequest Request { get; }
        public IAgentCatalog Catalog { get; }
        public AgentExecutionResult? LastResult { get; private set; }

        public void SetLastResult(AgentExecutionResult result) => LastResult = result;
    }

    /// <summary>
    /// Small helper to reuse the reflection-based AgentSession creation logic without duplicating it.
    /// </summary>
    private static class MultiTurnStructuredAgentClientAccessor
    {
        public static Func<AgentSession> CreateSessionFactory()
        {
            var ctor = typeof(ChatClientAgentSession).GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            if (ctor is null)
            {
                throw new InvalidOperationException("Failed to locate ChatClientAgentSession non-public constructor.");
            }

            return () =>
            {
                if (ctor.Invoke(null) is not AgentSession session)
                {
                    throw new InvalidOperationException("Failed to create AgentSession for ChatClientAgent runs.");
                }

                return session;
            };
        }
    }
}
