using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Agents.AI.Workflows.Declarative.Events;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

// Terminal-only POC:
// - One Master agent
// - One "workflow tool" with an HITL approval checkpoint
// - One specialist sub-agent tool
//
// Env vars:
// - OPENAI_API_KEY (required)
// - OPENAI_BASE_URL (optional; for OpenAI-compatible gateways)
// - OPENAI_MODEL (optional; default: gpt-4.1-mini)
// - OPENAI_SPECIALIST_MODEL (optional; default: OPENAI_MODEL)

var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(openAiKey))
{
    Console.Error.WriteLine("Missing OPENAI_API_KEY env var.");
    Console.Error.WriteLine("Example:");
    Console.Error.WriteLine("  export OPENAI_API_KEY=\"...\"");
    Environment.Exit(2);
}

var openAiBaseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
var model = Environment.GetEnvironmentVariable("OPENAI_MODEL");
if (string.IsNullOrWhiteSpace(model))
{
    model = "gpt-4.1-mini";
}

var specialistModel = Environment.GetEnvironmentVariable("OPENAI_SPECIALIST_MODEL");
if (string.IsNullOrWhiteSpace(specialistModel))
{
    specialistModel = model;
}

IChatClient masterChatClient = CreateChatClient(openAiKey!, openAiBaseUrl, model!);
IChatClient specialistChatClient = CreateChatClient(openAiKey!, openAiBaseUrl, specialistModel!);

var toolJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
};

// Conversation history (in-memory for the POC).
var conversation = new List<ChatMessage>();

// In-memory workflow engine (workflow runtime + checkpoint store) for the HITL demo.
var workflowEngine = new CapitalCallWorkflowEngine();

var masterAgent = CreateMasterAgent(
    masterChatClient,
    specialistChatClient,
    toolJsonOptions,
    workflowEngine);

Console.WriteLine("Microsoft Agent Framework POC");
Console.WriteLine("Type your request. Commands: /exit, /reset, /state");
Console.WriteLine();

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();
    if (input is null)
    {
        break;
    }

    input = input.Trim();
    if (input.Length == 0)
    {
        continue;
    }

    if (string.Equals(input, "/exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    if (string.Equals(input, "/reset", StringComparison.OrdinalIgnoreCase))
    {
        conversation.Clear();
        workflowEngine.Reset();
        Console.WriteLine("Reset conversation + workflow state.");
        Console.WriteLine();
        continue;
    }

    if (string.Equals(input, "/state", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine(workflowEngine.ToDebugString());
        Console.WriteLine();
        continue;
    }

    conversation.Add(new ChatMessage(ChatRole.User, input));

    // We create a fresh session for each run, but keep the user-visible transcript in-memory.
    // This is enough for a terminal POC and matches the "ephemeral session" pattern.
    var session = SessionHelpers.CreateSession();
    var messages = BuildMasterMessages(conversation);

    AgentResponse? response;
    try
    {
        response = await masterAgent.RunAsync(messages, session, options: null, cancellationToken: CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[error] {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine();
        continue;
    }

    var text = string.IsNullOrWhiteSpace(response?.Text) ? "(no response text)" : response!.Text.Trim();
    conversation.Add(new ChatMessage(ChatRole.Assistant, text));

    Console.WriteLine();
    Console.WriteLine(text);
    Console.WriteLine();
}

static IChatClient CreateChatClient(string apiKey, string? baseUrl, string model)
{
    OpenAIClient client = string.IsNullOrWhiteSpace(baseUrl)
        ? new OpenAIClient(new ApiKeyCredential(apiKey))
        : new OpenAIClient(
            credential: new ApiKeyCredential(apiKey),
            options: new OpenAIClientOptions { Endpoint = new Uri(baseUrl, UriKind.Absolute) });

    return client.GetChatClient(model).AsIChatClient();
}

static ChatClientAgent CreateMasterAgent(
    IChatClient masterChatClient,
    IChatClient specialistChatClient,
    JsonSerializerOptions toolJsonOptions,
    CapitalCallWorkflowEngine workflowEngine)
{
    var toolHost = new ToolHost(specialistChatClient, toolJsonOptions, workflowEngine);

    var tools = new List<AITool>
    {
        // "Workflow with HITL" tool (one tool, internally multi-step).
        AIFunctionFactory.Create(
            (string request, CancellationToken ct) => toolHost.RunCapitalCallWorkflowAsync(request, ct),
            new AIFunctionFactoryOptions
            {
                SerializerOptions = toolJsonOptions,
                Name = "capital_call_workflow",
                Description = "Runs a capital call workflow that pauses for human approval mid-way. Input is the user's request or approval message."
            }),

        // "Specialist sub-agent" tool.
        AIFunctionFactory.Create(
            (string task, CancellationToken ct) => toolHost.RunBoardPresentationSubAgentAsync(task, ct),
            new AIFunctionFactoryOptions
            {
                SerializerOptions = toolJsonOptions,
                Name = "board_presentation_agent",
                Description = "A specialist agent that writes a one-pager in board presentation format based on the given task."
            })
    };

    var options = new ChatClientAgentOptions
    {
        Id = "master-agent",
        Name = "Master agent",
        Description = "Selects and combines tools (workflows + specialist sub-agent) to satisfy the user's request.",
        ChatOptions = new ChatOptions
        {
            Tools = tools,
            ToolMode = ChatToolMode.Auto
        }
    };

    // services: null keeps this POC simple (no DI container).
    return new ChatClientAgent(masterChatClient, options, loggerFactory: null, services: null);
}

static ChatMessage[] BuildMasterMessages(IReadOnlyList<ChatMessage> transcript)
{
    const string systemPrompt =
        """
        You are a master agent. You can use tools to do work.

        Available tools:
        - capital_call_workflow: a workflow that may pause for human approval mid-way.
        - board_presentation_agent: a specialist agent for board-style one-pagers.

        Behavior:
        - Use any combination of tools to answer the user's request.
        - If a workflow returns a message asking for human approval, stop and ask the user to approve/reject in the chat.
        - If the user later replies with approval/rejection, call the workflow tool again with that message to resume.

        Output:
        - Return a single natural response (no internal ids / no tool names).
        """;

    var messages = new List<ChatMessage>(capacity: transcript.Count + 1)
    {
        new(ChatRole.System, systemPrompt)
    };

    messages.AddRange(transcript);
    return messages.ToArray();
}

file static class SessionHelpers
{
    public static AgentSession CreateSession()
    {
        // Microsoft.Agents.AI 1.0.0 keeps AgentSession constructors non-public.
        // We instantiate ChatClientAgentSession via reflection for this POC.
        var ctor = typeof(ChatClientAgentSession).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        if (ctor is null)
        {
            throw new InvalidOperationException("Failed to locate ChatClientAgentSession non-public constructor.");
        }

        if (ctor.Invoke(null) is not AgentSession session)
        {
            throw new InvalidOperationException("Failed to create AgentSession.");
        }

        return session;
    }
}

file sealed class ToolHost
{
    private readonly IChatClient _specialistChatClient;
    private readonly JsonSerializerOptions _toolJsonOptions;
    private readonly CapitalCallWorkflowEngine _workflowEngine;

    public ToolHost(IChatClient specialistChatClient, JsonSerializerOptions toolJsonOptions, CapitalCallWorkflowEngine workflowEngine)
    {
        _specialistChatClient = specialistChatClient;
        _toolJsonOptions = toolJsonOptions;
        _workflowEngine = workflowEngine;
    }

    // Tool 1: "Workflow" with an HITL checkpoint.
    public Task<string> RunCapitalCallWorkflowAsync(string request, CancellationToken cancellationToken) =>
        _workflowEngine.HandleAsync(request, cancellationToken);

    // Tool 2: "Sub-agent" (specialist).
    public async Task<string> RunBoardPresentationSubAgentAsync(string task, CancellationToken cancellationToken)
    {
        var agentOptions = new ChatClientAgentOptions
        {
            Id = "board-presentation-agent",
            Name = "Board Presentation Agent",
            Description = "Writes crisp board-style one-pagers.",
        };

        var agent = new ChatClientAgent(_specialistChatClient, agentOptions, loggerFactory: null, services: null);
        var session = SessionHelpers.CreateSession();

        var messages = new[]
        {
            new ChatMessage(ChatRole.System,
                """
                You write concise board-presentation one-pagers.
                Use headings and short bullets. Keep it executive-friendly.
                """),
            new ChatMessage(ChatRole.User, task)
        };

        var response = await agent.RunAsync(messages, session, options: null, cancellationToken: cancellationToken);
        return string.IsNullOrWhiteSpace(response?.Text) ? "(no text)" : response!.Text.Trim();
    }
}

file sealed class CapitalCallWorkflowEngine
{
    private static readonly JsonSerializerOptions CheckpointJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static readonly RequestPort<CapitalCallDraft, CapitalCallApproval> ApprovalPort =
        RequestPort.Create<CapitalCallDraft, CapitalCallApproval>("capital-call-approval");

    private readonly InMemoryJsonCheckpointStore _checkpointStore = new();
    private readonly CheckpointManager _checkpointManager;

    private string? _sessionId;
    private string? _checkpointId;
    private PendingApprovalRequest? _pending;

    public CapitalCallWorkflowEngine()
    {
        _checkpointManager = CheckpointManager.CreateJson(_checkpointStore, CheckpointJsonOptions);
    }

    public async Task<string> HandleAsync(string userText, CancellationToken cancellationToken)
    {
        userText = userText.Trim();
        if (userText.Length == 0)
        {
            return "I need a request or an approval decision.";
        }

        if (_pending is not null)
        {
            return await ResumeAsync(userText, cancellationToken);
        }

        return await StartAsync(userText, cancellationToken);
    }

    public void Reset()
    {
        _sessionId = null;
        _checkpointId = null;
        _pending = null;
        _checkpointStore.Reset();
    }

    public string ToDebugString()
    {
        var pending = _pending is null
            ? "(none)"
            : $"PortId={_pending.PortId} RequestId={_pending.RequestId}";

        return $"WorkflowEngine: SessionId={_sessionId ?? "(null)"} CheckpointId={_checkpointId ?? "(null)"} Pending={pending}";
    }

    private async Task<string> StartAsync(string requestText, CancellationToken cancellationToken)
    {
        _sessionId = Guid.NewGuid().ToString("N");
        _checkpointId = null;
        _pending = null;

        var workflow = BuildWorkflow();
        await using var run = await InProcessExecution.RunAsync(
            workflow,
            new CapitalCallStart(requestText),
            _checkpointManager,
            _sessionId,
            cancellationToken);

        return await ProcessRunAsync(run, cancellationToken);
    }

    private async Task<string> ResumeAsync(string userText, CancellationToken cancellationToken)
    {
        if (_sessionId is null || _checkpointId is null || _pending is null)
        {
            Reset();
            return "Workflow state was invalid. Please start again.";
        }

        var workflow = BuildWorkflow();
        var checkpoint = new CheckpointInfo(_sessionId, _checkpointId);
        await using var run = await InProcessExecution.ResumeAsync(workflow, checkpoint, _checkpointManager, cancellationToken);

        var decision = ParseDecision(userText, _pending.Draft);
        var externalRequest = ExternalRequest.Create(ApprovalPort, _pending.Draft, _pending.RequestId);
        var response = externalRequest.CreateResponse(decision);
        await run.ResumeAsync([response], cancellationToken);

        // Consume the pending request (the workflow may generate a new one, but this one is satisfied).
        _pending = null;

        return await ProcessRunAsync(run, cancellationToken);
    }

    private async Task<string> ProcessRunAsync(Run run, CancellationToken cancellationToken)
    {
        string? lastAssistantText = null;
        while (true)
        {
            var events = run.NewEvents.ToArray();
            if (events.Length == 0)
            {
                break;
            }

            foreach (var workflowEvent in events)
            {
                switch (workflowEvent)
                {
                    case SuperStepCompletedEvent superStepCompleted:
                    {
                        var checkpoint = superStepCompleted.CompletionInfo?.Checkpoint;
                        if (checkpoint is not null)
                        {
                            _checkpointId = checkpoint.CheckpointId;
                        }

                        break;
                    }
                    case WorkflowOutputEvent outputEvent:
                    {
                        if (outputEvent.Data is AgentResponse agentResponse && !string.IsNullOrWhiteSpace(agentResponse.Text))
                        {
                            lastAssistantText = agentResponse.Text.Trim();
                            break;
                        }

                        if (outputEvent.Data is string text)
                        {
                            lastAssistantText = text.Trim();
                            break;
                        }

                        if (outputEvent.Data is not null)
                        {
                            lastAssistantText = JsonSerializer.Serialize(outputEvent.Data, outputEvent.Data.GetType(), CheckpointJsonOptions);
                        }

                        break;
                    }
                    case RequestInfoEvent requestInfo:
                    {
                        // HITL boundary: the workflow is waiting for an external response to a request port.
                        // We store the request id + typed request payload so we can resume later.
                        if (requestInfo.Request.TryGetDataAs(typeof(CapitalCallDraft), out var typedRequest) &&
                            typedRequest is CapitalCallDraft draft)
                        {
                            _pending = new PendingApprovalRequest(
                                requestInfo.Request.PortInfo.PortId,
                                requestInfo.Request.RequestId,
                                draft);
                        }
                        else
                        {
                            _pending = new PendingApprovalRequest(
                                requestInfo.Request.PortInfo.PortId,
                                requestInfo.Request.RequestId,
                                new CapitalCallDraft("Draft unavailable (unexpected request payload type)."));
                        }

                        var draftText = _pending.Draft.DraftText;
                        return
                            "Human approval required.\n" +
                            "\n" +
                            "Draft:\n" +
                            draftText +
                            "\n\n" +
                            "Reply with APPROVE or REJECT (you can add notes after the word).";
                    }
                }
            }
        }

        var status = await run.GetStatusAsync(cancellationToken);
        return status switch
        {
            RunStatus.PendingRequests => _pending is null
                ? "Human approval required."
                : "Human approval required. Reply with APPROVE or REJECT.",
            _ => lastAssistantText ?? "Workflow completed."
        };
    }

    private static CapitalCallApproval ParseDecision(string userText, CapitalCallDraft draft)
    {
        var approved = userText.Contains("approve", StringComparison.OrdinalIgnoreCase);
        var rejected = userText.Contains("reject", StringComparison.OrdinalIgnoreCase);

        if (approved == rejected)
        {
            // Ambiguous or neither; default to reject to keep the demo safe.
            return new CapitalCallApproval(draft.DraftText, Approved: false, Notes: userText);
        }

        return new CapitalCallApproval(draft.DraftText, Approved: approved, Notes: userText);
    }

    private static Workflow BuildWorkflow()
    {
        var generateDraft = new GenerateCapitalCallDraftExecutor().BindExecutor();
        var approval = ApprovalPort.BindAsExecutor();
        var finalize = new FinalizeCapitalCallExecutor().BindExecutor();

        return new WorkflowBuilder(generateDraft)
            .WithName("Capital Call POC")
            .WithDescription("POC workflow that pauses for human approval and then finalizes output.")
            .AddEdge(generateDraft, approval, "draft -> approve", idempotent: true)
            .AddEdge(approval, finalize, "approve -> finalize", idempotent: true)
            .WithOutputFrom(finalize)
            .Build(validateOrphans: true);
    }

    private sealed class GenerateCapitalCallDraftExecutor : Executor<CapitalCallStart, CapitalCallDraft>
    {
        public GenerateCapitalCallDraftExecutor()
            : base("generate_draft")
        {
        }

        public override ValueTask<CapitalCallDraft> HandleAsync(
            CapitalCallStart input,
            IWorkflowContext context,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new CapitalCallDraft(
                "Capital Call Draft (POC)\n" +
                $"- Request: {input.RequestText}\n" +
                "- Allocations: (placeholder)\n" +
                "- Total: (placeholder)\n"));
    }

    private sealed class FinalizeCapitalCallExecutor : Executor<CapitalCallApproval, string>
    {
        public FinalizeCapitalCallExecutor()
            : base("finalize")
        {
        }

        public override ValueTask<string> HandleAsync(
            CapitalCallApproval input,
            IWorkflowContext context,
            CancellationToken cancellationToken)
        {
            var text = input.Approved
                ? "Approved. Final output generated: Capital Call LP Notice Workbook (POC placeholder)."
                : "Rejected. Tell me what to change and I’ll regenerate the draft for approval.";

            return ValueTask.FromResult(text);
        }
    }

    private sealed record PendingApprovalRequest(string PortId, string RequestId, CapitalCallDraft Draft);
    private sealed record CapitalCallStart(string RequestText);
    private sealed record CapitalCallDraft(string DraftText);
    private sealed record CapitalCallApproval(string DraftText, bool Approved, string? Notes);
}

file sealed class InMemoryJsonCheckpointStore : ICheckpointStore<JsonElement>
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Entry>> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public void Reset()
    {
        lock (_gate)
        {
            _sessions.Clear();
        }
    }

    public ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var entries))
            {
                return ValueTask.FromResult<IEnumerable<CheckpointInfo>>(Array.Empty<CheckpointInfo>());
            }

            IEnumerable<Entry> filtered = entries;
            if (withParent is not null)
            {
                filtered = filtered.Where(item => string.Equals(item.ParentCheckpointId, withParent.CheckpointId, StringComparison.OrdinalIgnoreCase));
            }

            var result = filtered
                .OrderBy(item => item.CreatedAtUtc)
                .Select(item => new CheckpointInfo(sessionId, item.CheckpointId))
                .ToArray();
            return ValueTask.FromResult<IEnumerable<CheckpointInfo>>(result);
        }
    }

    public ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent)
    {
        var checkpoint = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("N"));
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var entries))
            {
                entries = new List<Entry>();
                _sessions[sessionId] = entries;
            }

            entries.Add(new Entry
            {
                CheckpointId = checkpoint.CheckpointId,
                ParentCheckpointId = parent?.CheckpointId,
                PayloadJson = value.GetRawText(),
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
        }

        return ValueTask.FromResult(checkpoint);
    }

    public ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var entries))
            {
                throw new InvalidOperationException($"Checkpoint '{key.CheckpointId}' was not found for workflow session '{sessionId}'.");
            }

            var entry = entries.FirstOrDefault(item => string.Equals(item.CheckpointId, key.CheckpointId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new InvalidOperationException($"Checkpoint '{key.CheckpointId}' was not found for workflow session '{sessionId}'.");
            }

            using var doc = JsonDocument.Parse(entry.PayloadJson);
            return ValueTask.FromResult(doc.RootElement.Clone());
        }
    }

    private sealed class Entry
    {
        public string CheckpointId { get; init; } = string.Empty;
        public string? ParentCheckpointId { get; init; }
        public string PayloadJson { get; init; } = "{}";
        public DateTimeOffset CreatedAtUtc { get; init; }
    }
}
