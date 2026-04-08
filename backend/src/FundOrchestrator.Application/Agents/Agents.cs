using FundOrchestrator.Application.Abstractions;
using FundOrchestrator.Application.Support;
using FundOrchestrator.Contracts.Messaging;
using FundOrchestrator.Domain.Agents;
using FundOrchestrator.Domain.Auditing;
using FundOrchestrator.Domain.Conversations;
using FundOrchestrator.Domain.Files;
using FundOrchestrator.Domain.Operations;
using FundOrchestrator.Domain.Reviews;

namespace FundOrchestrator.Application.Agents;

public sealed class AgentCatalog : IAgentCatalog
{
    private readonly IReadOnlyDictionary<string, IAgent> _agents;

    public AgentCatalog(IEnumerable<IAgent> agents)
    {
        _agents = agents.ToDictionary(agent => agent.Definition.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<AgentDefinition> List() =>
        _agents.Values.Select(static agent => agent.Definition).ToArray();

    public IAgent Resolve(string agentId) =>
        _agents.TryGetValue(agentId, out var agent)
            ? agent
            : throw new InvalidOperationException($"Unknown agent '{agentId}'.");
}

public sealed class NoticeCreationAgent : IAgent
{
    private static readonly IReadOnlyCollection<AgentInputFieldDefinition> InputFields =
    [
        new("fundName", "fund name", "Name of the private equity fund for which the notice draft should be created.", true, "ABC Growth Fund II"),
        new("amount", "capital call amount", "Amount requested from LPs in the capital call notice.", true, "$5,000,000"),
        new("noticeDate", "notice date", "Date that should appear on the notice draft.", false, "April 15")
    ];

    private readonly IAgentInputCompletionService _inputCompletionService;

    public NoticeCreationAgent(IAgentInputCompletionService inputCompletionService)
    {
        _inputCompletionService = inputCompletionService;
    }

    public AgentDefinition Definition { get; } = new(
        AgentIds.NoticeCreation,
        "Notice Creation Helper",
        "Creates a draft capital call notice and opens the correct page with prefilled data.",
        AgentExecutionMode.InlineFunction);

    public Task<AgentExecutionResult> StartAsync(
        ConversationThread conversation,
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken) =>
        ExecuteAsync(conversationHistory, userMessage, operation, attachments, cancellationToken);

    public Task<AgentExecutionResult> ContinueAsync(
        ConversationThread conversation,
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken) =>
        ExecuteAsync(conversationHistory, userMessage, operation, attachments, cancellationToken);

    public Task<string> DescribeStatusAsync(
        AgentOperation operation,
        IReadOnlyCollection<ReviewTask> relatedTasks,
        CancellationToken cancellationToken)
    {
        var status = operation.Status == AgentOperationStatus.Completed
            ? "Notice draft is ready for review and can be opened directly."
            : operation.PendingClarification ?? "Waiting for the missing notice details.";
        return Task.FromResult(status);
    }

    private async Task<AgentExecutionResult> ExecuteAsync(
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        CancellationToken cancellationToken)
    {
        var payload = await CompleteInputsAsync(
            conversationHistory,
            userMessage,
            operation,
            attachments,
            cancellationToken);

        var fundName = AgentInputStateSupport.GetValue(payload, "fundName");
        var amount = AgentInputStateSupport.GetValue(payload, "amount");
        var missingRequiredFields = AgentInputStateSupport.GetMissingRequiredFieldNames(InputFields, payload);

        operation.Title = string.IsNullOrWhiteSpace(fundName)
            ? "Capital Call Draft"
            : $"Capital Call Draft for {fundName}";

        if (missingRequiredFields.Count > 0)
        {
            operation.Status = AgentOperationStatus.ClarificationRequired;
            operation.CurrentStep = "CollectNoticeInputs";
            operation.PendingClarification = AgentInputStateSupport.BuildPendingClarification(InputFields, missingRequiredFields, "build the notice draft");
            operation.Summary = "Waiting for the missing notice inputs.";
            operation.DataJson = AgentInputStateSupport.SerializeValues(payload);

            return new AgentExecutionResult(
                AgentInputStateSupport.BuildAssistantClarificationMessage(InputFields, missingRequiredFields, "notice creation"),
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.AskForMoreInfo,
                    Label = "Provide missing notice details",
                    PayloadJson = JsonContent.Serialize(new { required = missingRequiredFields })
                }],
                BuildAudit(operation, "NoticeClarificationRequested", payload));
        }

        operation.Status = AgentOperationStatus.Completed;
        operation.CurrentStep = "DraftReady";
        operation.PendingClarification = null;
        operation.Summary = "Created a reversible draft notice with prefilled fund, amount, and notice defaults.";
        payload["draftRoute"] = $"/funds/{AgentInputStateSupport.Slugify(fundName!)}/capital-call-drafts/{operation.Id}";
        payload["lpCount"] = "24";
        payload["defaultNoticeType"] = "Capital Call";
        operation.DataJson = AgentInputStateSupport.SerializeValues(payload);

        return new AgentExecutionResult(
            $"I created a draft capital call for {fundName} and prefilled the amount of {amount}. You can open it, review the LP terms, and edit anything before sending.",
            operation,
            [
                new AgentAction
                {
                    Type = AgentActionType.OpenPageWithPrefill,
                    Label = "Open draft",
                    Route = payload["draftRoute"],
                    PayloadJson = JsonContent.Serialize(payload)
                }
            ],
            BuildAudit(operation, "NoticeDraftCreated", payload));
    }

    private async Task<Dictionary<string, string?>> CompleteInputsAsync(
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        CancellationToken cancellationToken)
    {
        var currentValues = AgentInputStateSupport.LoadValues(operation);
        var completion = await _inputCompletionService.CompleteAsync(
            new AgentInputCompletionRequest(
                Definition.Id,
                Definition.DisplayName,
                operation.CurrentStep,
                operation.PendingClarification,
                userMessage.Content,
                AgentInputStateSupport.GetRelevantConversationHistory(conversationHistory, userMessage, operation),
                attachments,
                currentValues,
                InputFields),
            cancellationToken);

        return new Dictionary<string, string?>(completion.Values, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyCollection<AuditEvent> BuildAudit(AgentOperation operation, string eventType, Dictionary<string, string?> payload) =>
    [
        new AuditEvent
        {
            TenantId = operation.TenantId,
            ConversationId = operation.ConversationId,
            OperationId = operation.Id,
            EventType = eventType,
            ActorType = "Agent",
            ActorId = AgentIds.NoticeCreation,
            DataJson = JsonContent.Serialize(payload)
        }
    ];
}

public sealed class OnePagerAgent : IAgent
{
    private static readonly IReadOnlyCollection<AgentInputFieldDefinition> InputFields =
    [
        new("companyName", "company name", "Portfolio company name that should be used in the one-pager.", true, "Atlas Industrial"),
        new("formatName", "format name", "Preferred uploaded format or template name for the one-pager.", false, "Board Presentation Format")
    ];

    private readonly IAgentInputCompletionService _inputCompletionService;

    public OnePagerAgent(IAgentInputCompletionService inputCompletionService)
    {
        _inputCompletionService = inputCompletionService;
    }

    public AgentDefinition Definition { get; } = new(
        AgentIds.OnePager,
        "One Pager Generation Agent",
        "Builds a one-pager draft using internal portfolio data and the selected format.",
        AgentExecutionMode.InlineFunction);

    public Task<AgentExecutionResult> StartAsync(
        ConversationThread conversation,
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken) =>
        ExecuteAsync(conversationHistory, userMessage, operation, attachments, cancellationToken);

    public Task<AgentExecutionResult> ContinueAsync(
        ConversationThread conversation,
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken) =>
        ExecuteAsync(conversationHistory, userMessage, operation, attachments, cancellationToken);

    public Task<string> DescribeStatusAsync(
        AgentOperation operation,
        IReadOnlyCollection<ReviewTask> relatedTasks,
        CancellationToken cancellationToken) =>
        Task.FromResult(operation.Summary);

    private async Task<AgentExecutionResult> ExecuteAsync(
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        CancellationToken cancellationToken)
    {
        var payload = await CompleteInputsAsync(conversationHistory, userMessage, operation, attachments, cancellationToken);
        AgentInputStateSupport.MergeIfPresent(payload, "formatName", attachments.FirstOrDefault()?.FileName);

        var companyName = AgentInputStateSupport.GetValue(payload, "companyName");
        var missingRequiredFields = AgentInputStateSupport.GetMissingRequiredFieldNames(InputFields, payload);

        operation.Title = string.IsNullOrWhiteSpace(companyName)
            ? "One Pager Draft"
            : $"{companyName} One Pager";

        if (missingRequiredFields.Count > 0)
        {
            operation.Status = AgentOperationStatus.ClarificationRequired;
            operation.CurrentStep = "CollectCompany";
            operation.PendingClarification = AgentInputStateSupport.BuildPendingClarification(InputFields, missingRequiredFields, "generate the one-pager");
            operation.Summary = "Waiting for the target company.";
            operation.DataJson = AgentInputStateSupport.SerializeValues(payload);

            return new AgentExecutionResult(
                AgentInputStateSupport.BuildAssistantClarificationMessage(InputFields, missingRequiredFields, "one-pager generation"),
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.AskForMoreInfo,
                    Label = "Provide one-pager inputs",
                    PayloadJson = JsonContent.Serialize(new { required = missingRequiredFields })
                }],
                BuildAudit(operation, "OnePagerClarificationRequested", payload));
        }

        payload["templateName"] = payload.GetValueOrDefault("formatName", "Executive Snapshot Template");
        payload["artifactRoute"] = $"/artifacts/one-pager/{operation.Id}";
        payload["generatedAt"] = DateTimeOffset.UtcNow.ToString("u");
        payload["teaser"] = $"{companyName} revenue grew 18% YoY with stable margin expansion in the last reviewed quarter.";

        operation.Status = AgentOperationStatus.Completed;
        operation.CurrentStep = "ArtifactReady";
        operation.PendingClarification = null;
        operation.Summary = "Generated a draft one-pager using internal operating and ownership data.";
        operation.DataJson = AgentInputStateSupport.SerializeValues(payload);

        return new AgentExecutionResult(
            $"I generated a draft one-pager for {companyName} using {payload["templateName"]}. You can open it, edit it, and export when you are ready.",
            operation,
            [
                new AgentAction
                {
                    Type = AgentActionType.DownloadArtifact,
                    Label = "Open one-pager draft",
                    Route = payload["artifactRoute"],
                    PayloadJson = JsonContent.Serialize(payload)
                }
            ],
            BuildAudit(operation, "OnePagerGenerated", payload));
    }

    private async Task<Dictionary<string, string?>> CompleteInputsAsync(
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        CancellationToken cancellationToken)
    {
        var currentValues = AgentInputStateSupport.LoadValues(operation);
        var completion = await _inputCompletionService.CompleteAsync(
            new AgentInputCompletionRequest(
                Definition.Id,
                Definition.DisplayName,
                operation.CurrentStep,
                operation.PendingClarification,
                userMessage.Content,
                AgentInputStateSupport.GetRelevantConversationHistory(conversationHistory, userMessage, operation),
                attachments,
                currentValues,
                InputFields),
            cancellationToken);

        return new Dictionary<string, string?>(completion.Values, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyCollection<AuditEvent> BuildAudit(AgentOperation operation, string eventType, Dictionary<string, string?> payload) =>
    [
        new AuditEvent
        {
            TenantId = operation.TenantId,
            ConversationId = operation.ConversationId,
            OperationId = operation.Id,
            EventType = eventType,
            ActorType = "Agent",
            ActorId = AgentIds.OnePager,
            DataJson = JsonContent.Serialize(payload)
        }
    ];
}

public sealed class FundOnboardingAgent : IAgent
{
    private readonly IWorkflowCommandDispatcher _workflowCommandDispatcher;

    public FundOnboardingAgent(IWorkflowCommandDispatcher workflowCommandDispatcher)
    {
        _workflowCommandDispatcher = workflowCommandDispatcher;
    }

    public AgentDefinition Definition { get; } = new(
        AgentIds.FundOnboarding,
        "Fund Onboarding Helper",
        "Starts the onboarding workflow, waits for external classification, and resumes after human review.",
        AgentExecutionMode.SagaWorkflow);

    public async Task<AgentExecutionResult> StartAsync(
        ConversationThread conversation,
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (attachments.Count == 0)
        {
            operation.Status = AgentOperationStatus.ClarificationRequired;
            operation.CurrentStep = "AwaitingDocuments";
            operation.PendingClarification = "Upload the fund agreements or onboarding documents to start the workflow.";
            operation.Summary = "Waiting for onboarding documents.";

            return new AgentExecutionResult(
                "I can start the onboarding workflow as soon as you upload the agreements or transaction documents.",
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.AskForMoreInfo,
                    Label = "Upload onboarding documents"
                }],
                BuildAudit(operation, "OnboardingClarificationRequested", new { needs = "documents" }));
        }

        operation.Title = "Fund Onboarding Workflow";
        operation.Status = AgentOperationStatus.WaitingForExternalSystem;
        operation.CurrentStep = "DocumentClassification";
        operation.PendingClarification = null;
        operation.Summary = "Documents submitted to the onboarding service. Waiting for classification review.";
        operation.DataJson = JsonContent.Serialize(new
        {
            attachments = attachments.Select(asset => new { asset.Id, asset.FileName }),
            phase = "classification"
        });

        await _workflowCommandDispatcher.StartOnboardingAsync(
            new StartOnboardingWorkflowCommand
            {
                OperationId = operation.Id,
                ConversationId = conversation.Id,
                TenantId = context.TenantId,
                UserId = context.UserId,
                AttachmentIds = attachments.Select(asset => asset.Id).ToList()
            },
            cancellationToken);

        return new AgentExecutionResult(
            $"I started the onboarding workflow with {attachments.Count} document(s). I’ll keep the same conversation updated as classification and extraction reviews become available.",
            operation,
            [new AgentAction
            {
                Type = AgentActionType.StartAsyncOperation,
                Label = "Track onboarding workflow",
                PayloadJson = JsonContent.Serialize(new { operationId = operation.Id, status = operation.Status.ToString() })
            }],
            BuildAudit(operation, "OnboardingWorkflowStarted", new
            {
                attachmentIds = attachments.Select(asset => asset.Id).ToArray()
            }),
            "Workflow updates will appear here without interrupting other work in this chat.");
    }

    public Task<AgentExecutionResult> ContinueAsync(
        ConversationThread conversation,
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (operation.Status == AgentOperationStatus.ClarificationRequired && attachments.Count > 0)
        {
            return StartAsync(conversation, conversationHistory, userMessage, operation, attachments, context, cancellationToken);
        }

        return Task.FromResult(new AgentExecutionResult(
            "The onboarding workflow is already in progress. I’ll keep the conversation updated when the next review task or draft is ready.",
            operation,
            [new AgentAction
            {
                Type = AgentActionType.ShowStatus,
                Label = "View onboarding status",
                PayloadJson = JsonContent.Serialize(new { operationId = operation.Id, status = operation.Status.ToString() })
            }]));
    }

    public Task<string> DescribeStatusAsync(
        AgentOperation operation,
        IReadOnlyCollection<ReviewTask> relatedTasks,
        CancellationToken cancellationToken)
    {
        if (operation.Status == AgentOperationStatus.WaitingForHumanReview)
        {
            var openTask = relatedTasks.FirstOrDefault(task => task.Status == ReviewTaskStatus.Open);
            return Task.FromResult(openTask is null
                ? "Waiting for the review queue to refresh."
                : $"{openTask.Title} is waiting for review.");
        }

        if (operation.Status == AgentOperationStatus.WaitingForExternalSystem)
        {
            return Task.FromResult("Waiting for the external onboarding service to finish the current stage.");
        }

        return Task.FromResult(operation.Summary);
    }

    private static IReadOnlyCollection<AuditEvent> BuildAudit(AgentOperation operation, string eventType, object payload) =>
    [
        new AuditEvent
        {
            TenantId = operation.TenantId,
            ConversationId = operation.ConversationId,
            OperationId = operation.Id,
            EventType = eventType,
            ActorType = "Agent",
            ActorId = AgentIds.FundOnboarding,
            DataJson = JsonContent.Serialize(payload)
        }
    ];
}

internal static class AgentInputStateSupport
{
    public static Dictionary<string, string?> LoadValues(AgentOperation operation)
    {
        var payload = JsonContent.Deserialize<Dictionary<string, string?>>(operation.DataJson);
        return payload is null
            ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(payload, StringComparer.OrdinalIgnoreCase);
    }

    public static string SerializeValues(IDictionary<string, string?> payload)
    {
        var compact = payload
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        return JsonContent.Serialize(compact);
    }

    public static IReadOnlyCollection<string> GetMissingRequiredFieldNames(
        IReadOnlyCollection<AgentInputFieldDefinition> fieldDefinitions,
        IReadOnlyDictionary<string, string?> payload) =>
        fieldDefinitions
            .Where(static field => field.Required)
            .Where(field => !payload.TryGetValue(field.Name, out var value) || string.IsNullOrWhiteSpace(value))
            .Select(field => field.Name)
            .ToArray();

    public static string BuildPendingClarification(
        IReadOnlyCollection<AgentInputFieldDefinition> fieldDefinitions,
        IReadOnlyCollection<string> missingFieldNames,
        string actionDescription) =>
        $"I still need {FormatFieldLabels(fieldDefinitions, missingFieldNames)} to {actionDescription}.";

    public static string BuildAssistantClarificationMessage(
        IReadOnlyCollection<AgentInputFieldDefinition> fieldDefinitions,
        IReadOnlyCollection<string> missingFieldNames,
        string capabilityDescription) =>
        $"I can handle {capabilityDescription}, but I still need {FormatFieldLabels(fieldDefinitions, missingFieldNames)}.";

    public static IReadOnlyCollection<ConversationMessage> GetRelevantConversationHistory(
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation) =>
        conversationHistory
            .Where(message => message.OperationId == operation.Id || message.Id == userMessage.Id)
            .OrderByDescending(message => message.CreatedAtUtc)
            .Take(8)
            .OrderBy(message => message.CreatedAtUtc)
            .ToArray();

    public static string? GetValue(IReadOnlyDictionary<string, string?> payload, string key) =>
        payload.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    public static void MergeIfPresent(IDictionary<string, string?> payload, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            payload[key] = value.Trim();
        }
    }

    public static string Slugify(string value) =>
        value.Trim().ToLowerInvariant().Replace(' ', '-');

    private static string FormatFieldLabels(
        IReadOnlyCollection<AgentInputFieldDefinition> fieldDefinitions,
        IReadOnlyCollection<string> missingFieldNames)
    {
        var labels = missingFieldNames
            .Select(name => fieldDefinitions.FirstOrDefault(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Label ?? name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return labels.Length switch
        {
            0 => "a little more detail",
            1 => $"the {labels[0]}",
            2 => $"the {labels[0]} and {labels[1]}",
            _ => $"these details: {string.Join(", ", labels[..^1])}, and {labels[^1]}"
        };
    }
}
