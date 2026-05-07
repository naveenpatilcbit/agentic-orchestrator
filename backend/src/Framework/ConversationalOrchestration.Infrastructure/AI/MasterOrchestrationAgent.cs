using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Conversations;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Agents;
using ConversationalOrchestration.Domain.Auditing;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using ConversationalOrchestration.Infrastructure.AI.Tools;
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
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
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
    private readonly ICurrencyConversionService _currencyConversionService;
    private readonly IAuditEventRepository _auditEventRepository;
    private readonly IAgentOperationRepository _operationRepository;
    private readonly IOperationOutputRepository _operationOutputRepository;
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
        ICurrencyConversionService currencyConversionService,
        IAuditEventRepository auditEventRepository,
        IAgentOperationRepository operationRepository,
        IOperationOutputRepository operationOutputRepository,
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
        _currencyConversionService = currencyConversionService;
        _auditEventRepository = auditEventRepository;
        _operationRepository = operationRepository;
        _operationOutputRepository = operationOutputRepository;
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
            runState.LastResult?.Operation.Id,
            runState.LastResult?.Actions);
    }

    private IList<AITool> BuildTools(MasterAgentRunState state)
    {
        // The master agent is just a tool host: every registered "agent" (workflow capability)
        // is exposed as exactly one tool. HITL and workflow state machines live inside each workflow.
        // We intentionally do NOT expose internal plumbing tools like "continue active operation".
        var tools = new List<AITool>();

        tools.Add(AIFunctionFactory.Create(
            (CurrencyConversionToolRequest input, CancellationToken ct) => CurrencyConversionServiceAsync(input, state, ct),
            new AIFunctionFactoryOptions
            {
                SerializerOptions = JsonOptions,
                Name = "currencyConversionService",
                Description = "Dummy currency conversion service. Input: fromCurrency, toCurrency, date. Output: conversionFactor."
            }));

        foreach (var agent in state.Catalog.List())
        {
            var toolName = SanitizeFunctionName(agent.Id);
            tools.Add(AIFunctionFactory.Create(
                (Dictionary<string, string?>? seedValues, CancellationToken ct) => RunAgentAsync(agent.Id, seedValues, state, ct),
                new AIFunctionFactoryOptions
                {
                    SerializerOptions = JsonOptions,
                    Name = toolName,
                    Description = $"{agent.DisplayName}. {agent.Description} Optionally provide seedValues to pre-fill required inputs."
                }));
        }

        return tools;
    }

    private async Task<CurrencyConversionToolResponse> CurrencyConversionServiceAsync(
        CurrencyConversionToolRequest input,
        MasterAgentRunState state,
        CancellationToken cancellationToken)
    {
        var date = input.Date == default ? DateOnly.FromDateTime(DateTime.UtcNow) : input.Date;

        var quote = await _currencyConversionService.GetConversionFactorAsync(
            input.FromCurrency,
            input.ToCurrency,
            date,
            cancellationToken);

        await _auditEventRepository.AddAsync(
            new AuditEvent
            {
                TenantId = state.Request.Context.TenantId,
                ConversationId = state.Request.Conversation.Id,
                OperationId = null,
                EventType = "CurrencyConversionToolInvoked",
                ActorType = "Tool",
                ActorId = "currencyConversionService",
                DataJson = JsonContent.Serialize(new
                {
                    fromCurrency = quote.FromCurrency,
                    toCurrency = quote.ToCurrency,
                    date = quote.Date,
                    conversionFactor = quote.ConversionFactor
                })
            },
            cancellationToken);

        return new CurrencyConversionToolResponse(
            quote.FromCurrency,
            quote.ToCurrency,
            quote.Date,
            quote.ConversionFactor);
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
        Dictionary<string, string?>? seedValues,
        MasterAgentRunState state,
        CancellationToken cancellationToken)
    {
        var request = state.Request;
        var conversation = request.Conversation;

        if (string.IsNullOrWhiteSpace(agentId))
        {
            return "I need an agent id to start new work.";
        }

        var agent = _agentCatalog.Resolve(agentId);
        var values = seedValues is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(seedValues, StringComparer.OrdinalIgnoreCase);

        // If the agent requires a completed source output and none was provided, try to use the latest compatible one.
        if (agent.Definition.SourceRequirements?.RequiresSource == true &&
            !values.ContainsKey(StandardAgentInputNames.SourceOutputId))
        {
            var source = await TryResolveLatestCompatibleSourceAsync(agent.Definition, conversation.Id, request.Context.TenantId, cancellationToken);
            if (source is not null)
            {
                values[StandardAgentInputNames.SourceOutputId] = source.Value.OutputId;
                values[StandardAgentInputNames.SourceOperationId] = source.Value.OperationId;
            }
        }

        var operation = new AgentOperation
        {
            TenantId = request.Context.TenantId,
            ConversationId = conversation.Id,
            AgentId = agent.Definition.Id,
            Title = agent.Definition.DisplayName,
            CreatedByUserId = request.Context.UserId,
            Status = AgentOperationStatus.Received,
            DataJson = values.Count == 0 ? null : JsonContent.Serialize(values)
        };

        await _operationRepository.UpsertAsync(operation, cancellationToken);

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

        await PersistOperationStateAsync(conversation, result.Operation, cancellationToken);
        state.SetLastResult(result);
        return result.AssistantMessage;
    }

    private async Task<(string OutputId, string OperationId)?> TryResolveLatestCompatibleSourceAsync(
        AgentDefinition definition,
        string conversationId,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var accepted = definition.SourceRequirements?.AcceptedOutputTypes;
        if (accepted is null || accepted.Count == 0)
        {
            return null;
        }

        var outputs = await _operationOutputRepository.ListByConversationAsync(conversationId, tenantId, cancellationToken);
        var match = outputs
            .Where(output => accepted.Contains(output.OutputType, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(output => output.UpdatedAtUtc)
            .FirstOrDefault();

        return match is null ? null : (match.Id, match.OperationId);
    }

    private async Task<string> RunAgentAsync(
        string agentId,
        Dictionary<string, string?>? seedValues,
        MasterAgentRunState state,
        CancellationToken cancellationToken)
    {
        var request = state.Request;
        var conversation = request.Conversation;

        if (string.IsNullOrWhiteSpace(agentId))
        {
            return "I need a workflow id to run.";
        }

        // Prefer continuing the latest in-progress operation for this workflow within the conversation.
        // If none exists, start a fresh operation.
        var operations = await _operationRepository.ListByConversationAsync(conversation.Id, request.Context.TenantId, cancellationToken);
        var existing = operations
            .Where(operation => string.Equals(operation.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(operation => operation.UpdatedAtUtc)
            .FirstOrDefault(operation => operation.Status is not AgentOperationStatus.Completed
                and not AgentOperationStatus.Failed
                and not AgentOperationStatus.Cancelled);

        if (existing is null)
        {
            return await StartAgentOperationAsync(agentId, seedValues, state, cancellationToken);
        }

        var agent = _agentCatalog.Resolve(existing.AgentId);
        var relatedReviews = request.ReviewTasks.Where(task => task.OperationId == existing.Id).ToArray();

        // HITL boundary: the workflow is blocked on a review/approval. Don't attempt to "continue" by tool calls.
        if (existing.Status == AgentOperationStatus.WaitingForHumanReview)
        {
            return await agent.DescribeStatusAsync(existing, relatedReviews, cancellationToken);
        }

        // Only call Continue when the workflow explicitly asked the user for more info.
        if (existing.Status != AgentOperationStatus.ClarificationRequired)
        {
            return await agent.DescribeStatusAsync(existing, relatedReviews, cancellationToken);
        }

        // Bind the user message to this operation so operation-scoped history compaction works.
        request.UserMessage.OperationId = existing.Id;
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
                existing,
                request.Attachments,
                request.Context,
                cancellationToken);
        }
        catch (Exception exception)
        {
            existing.Status = AgentOperationStatus.Failed;
            existing.CurrentStep = "ContinueFailed";
            existing.Summary = exception.Message;
            existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _operationRepository.UpsertAsync(existing, cancellationToken);

            return $"I couldn't continue {agent.Definition.DisplayName}: {exception.Message}";
        }

        await PersistOperationStateAsync(conversation, result.Operation, cancellationToken);
        state.SetLastResult(result);
        return result.AssistantMessage;
    }

    private async Task PersistOperationStateAsync(
        ConversationThread conversation,
        AgentOperation operation,
        CancellationToken cancellationToken)
    {
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _operationRepository.UpsertAsync(operation, cancellationToken);

        // Touch the conversation for list ordering without treating it as an operation pointer.
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _conversationRepository.UpsertAsync(conversation, cancellationToken);
    }

    private static string BuildSystemPrompt() =>
        """
        You are the Master agent for an enterprise workflow automation chat app.

        You can execute work by calling tools.
        - Each tool represents a workflow capability.
        - Use tools to run workflows and gather missing inputs.
        - Multiple operations can exist in the same conversation. When the user requests a multi-step sequence, keep it sequential and do not start later steps until prerequisites are satisfied.
        - If a step requires human review/approval, stop and instruct the user to complete the approval. Do not claim completion until approval is done.

        Output rules:
        - After tool calls, respond with one concise user-facing message.
        - Do not mention internal tool names, ids, or implementation details unless the user asked.
        - Do not produce multiple messages. One response only.
        """;

    private static string BuildUserPrompt(MasterOrchestrationRequest request, IAgentCatalog agentCatalog)
    {
        // Always include catalog agents so the LLM can pick the right workflow tool.
        // Note: We keep this payload compact; full conversation history is loaded via the history provider.
        var payload = new
        {
            message = request.Message,
            conversation = new
            {
                request.Conversation.Id,
                request.Conversation.Title,
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
                toolName = SanitizeFunctionName(agent.Id),
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
