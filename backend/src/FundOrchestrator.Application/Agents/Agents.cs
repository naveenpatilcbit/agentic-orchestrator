using System.Text.RegularExpressions;
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

    public AgentDefinition? TryClassifyNewWork(string userMessage, bool hasAttachments)
    {
        var normalized = userMessage.ToLowerInvariant();

        if (normalized.Contains("capital call") || normalized.Contains("notice"))
        {
            return Resolve(AgentIds.NoticeCreation).Definition;
        }

        if (normalized.Contains("one pager") || normalized.Contains("one-pager") || normalized.Contains("presentation"))
        {
            return Resolve(AgentIds.OnePager).Definition;
        }

        if (hasAttachments || normalized.Contains("onboard") || normalized.Contains("fund creation") || normalized.Contains("agreement"))
        {
            return Resolve(AgentIds.FundOnboarding).Definition;
        }

        return null;
    }
}

public sealed class NoticeCreationAgent : IAgent
{
    public AgentDefinition Definition { get; } = new(
        AgentIds.NoticeCreation,
        "Notice Creation Helper",
        "Creates a draft capital call notice and opens the correct page with prefilled data.",
        AgentExecutionMode.InlineFunction,
        ["capital call", "notice", "lp contribution", "notice draft"]);

    public Task<AgentExecutionResult> StartAsync(
        ConversationThread conversation,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken) =>
        ExecuteAsync(userMessage, operation);

    public Task<AgentExecutionResult> ContinueAsync(
        ConversationThread conversation,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken) =>
        ExecuteAsync(userMessage, operation);

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

    private static Task<AgentExecutionResult> ExecuteAsync(ConversationMessage userMessage, AgentOperation operation)
    {
        var payload = JsonContent.Deserialize<Dictionary<string, string>>(operation.DataJson) ?? [];

        MergeIfPresent(payload, "fundName", ExtractFundName(userMessage.Content));
        MergeIfPresent(payload, "amount", ExtractAmount(userMessage.Content));
        MergeIfPresent(payload, "noticeDate", ExtractDate(userMessage.Content));

        operation.Title = string.IsNullOrWhiteSpace(payload.GetValueOrDefault("fundName"))
            ? "Capital Call Draft"
            : $"Capital Call Draft for {payload["fundName"]}";

        if (!payload.ContainsKey("fundName") || !payload.ContainsKey("amount"))
        {
            operation.Status = AgentOperationStatus.ClarificationRequired;
            operation.CurrentStep = "CollectNoticeInputs";
            operation.PendingClarification = "I need the fund name and amount to build the notice draft.";
            operation.Summary = "Waiting for the missing notice inputs.";
            operation.DataJson = JsonContent.Serialize(payload);

            return Task.FromResult(new AgentExecutionResult(
                "I can handle the notice creation, but I still need the fund name and the capital call amount.",
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.AskForMoreInfo,
                    Label = "Provide missing notice details",
                    PayloadJson = JsonContent.Serialize(new { required = new[] { "fundName", "amount" } })
                }],
                BuildAudit(operation, "NoticeClarificationRequested", payload)));
        }

        operation.Status = AgentOperationStatus.Completed;
        operation.CurrentStep = "DraftReady";
        operation.PendingClarification = null;
        operation.Summary = "Created a reversible draft notice with prefilled fund, amount, and notice defaults.";
        payload["draftRoute"] = $"/funds/{Slugify(payload["fundName"])}/capital-call-drafts/{operation.Id}";
        payload["lpCount"] = "24";
        payload["defaultNoticeType"] = "Capital Call";
        operation.DataJson = JsonContent.Serialize(payload);

        return Task.FromResult(new AgentExecutionResult(
            $"I created a draft capital call for {payload["fundName"]} and prefilled the amount of {payload["amount"]}. You can open it, review the LP terms, and edit anything before sending.",
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
            BuildAudit(operation, "NoticeDraftCreated", payload)));
    }

    private static string? ExtractFundName(string input)
    {
        var match = Regex.Match(input, @"fund\s+(?<value>[a-z0-9][a-z0-9\s\-]{1,40})", RegexOptions.IgnoreCase);
        return match.Success ? ToTitleCase(match.Groups["value"].Value.Trim()) : null;
    }

    private static string? ExtractAmount(string input)
    {
        var match = Regex.Match(input, @"(?<value>\$?\d[\d,]*(?:\.\d+)?)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static string? ExtractDate(string input)
    {
        var match = Regex.Match(input, @"\b(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\s+\d{1,2}\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.Trim() : null;
    }

    private static IReadOnlyCollection<AuditEvent> BuildAudit(AgentOperation operation, string eventType, Dictionary<string, string> payload) =>
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

    private static void MergeIfPresent(IDictionary<string, string> payload, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            payload[key] = value;
        }
    }

    private static string ToTitleCase(string input) =>
        string.Join(" ", input.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));

    private static string Slugify(string value) =>
        value.Trim().ToLowerInvariant().Replace(' ', '-');
}

public sealed class OnePagerAgent : IAgent
{
    public AgentDefinition Definition { get; } = new(
        AgentIds.OnePager,
        "One Pager Generation Agent",
        "Builds a one-pager draft using internal portfolio data and the selected format.",
        AgentExecutionMode.InlineFunction,
        ["one pager", "one-pager", "presentation", "company snapshot"]);

    public Task<AgentExecutionResult> StartAsync(
        ConversationThread conversation,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken) =>
        ExecuteAsync(userMessage, operation, attachments);

    public Task<AgentExecutionResult> ContinueAsync(
        ConversationThread conversation,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken) =>
        ExecuteAsync(userMessage, operation, attachments);

    public Task<string> DescribeStatusAsync(
        AgentOperation operation,
        IReadOnlyCollection<ReviewTask> relatedTasks,
        CancellationToken cancellationToken) =>
        Task.FromResult(operation.Summary);

    private static Task<AgentExecutionResult> ExecuteAsync(
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments)
    {
        var payload = JsonContent.Deserialize<Dictionary<string, string>>(operation.DataJson) ?? [];
        MergeIfPresent(payload, "companyName", ExtractCompany(userMessage.Content));
        MergeIfPresent(payload, "formatName", attachments.FirstOrDefault()?.FileName);

        operation.Title = string.IsNullOrWhiteSpace(payload.GetValueOrDefault("companyName"))
            ? "One Pager Draft"
            : $"{payload["companyName"]} One Pager";

        if (!payload.ContainsKey("companyName"))
        {
            operation.Status = AgentOperationStatus.ClarificationRequired;
            operation.CurrentStep = "CollectCompany";
            operation.PendingClarification = "Tell me which portfolio company should be used for the one-pager.";
            operation.Summary = "Waiting for the target company.";
            operation.DataJson = JsonContent.Serialize(payload);

            return Task.FromResult(new AgentExecutionResult(
                "I can generate the one-pager, but I still need the company name.",
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.AskForMoreInfo,
                    Label = "Provide company name"
                }],
                BuildAudit(operation, "OnePagerClarificationRequested", payload)));
        }

        payload["templateName"] = payload.GetValueOrDefault("formatName", "Executive Snapshot Template");
        payload["artifactRoute"] = $"/artifacts/one-pager/{operation.Id}";
        payload["generatedAt"] = DateTimeOffset.UtcNow.ToString("u");
        payload["teaser"] = $"{payload["companyName"]} revenue grew 18% YoY with stable margin expansion in the last reviewed quarter.";

        operation.Status = AgentOperationStatus.Completed;
        operation.CurrentStep = "ArtifactReady";
        operation.PendingClarification = null;
        operation.Summary = "Generated a draft one-pager using internal operating and ownership data.";
        operation.DataJson = JsonContent.Serialize(payload);

        return Task.FromResult(new AgentExecutionResult(
            $"I generated a draft one-pager for {payload["companyName"]} using {payload["templateName"]}. You can open it, edit it, and export when you are ready.",
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
            BuildAudit(operation, "OnePagerGenerated", payload)));
    }

    private static string? ExtractCompany(string input)
    {
        var explicitMatch = Regex.Match(input, @"company\s+(?<value>[a-z0-9][a-z0-9\s\-]{1,40})", RegexOptions.IgnoreCase);
        if (explicitMatch.Success)
        {
            return ToTitleCase(explicitMatch.Groups["value"].Value.Trim());
        }

        var fallback = Regex.Match(input, @"for\s+(?<value>[a-z0-9][a-z0-9\s\-]{1,40})", RegexOptions.IgnoreCase);
        return fallback.Success ? ToTitleCase(fallback.Groups["value"].Value.Trim()) : null;
    }

    private static IReadOnlyCollection<AuditEvent> BuildAudit(AgentOperation operation, string eventType, Dictionary<string, string> payload) =>
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

    private static void MergeIfPresent(IDictionary<string, string> payload, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            payload[key] = value;
        }
    }

    private static string ToTitleCase(string input) =>
        string.Join(" ", input.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
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
        AgentExecutionMode.SagaWorkflow,
        ["onboarding", "fund creation", "documents", "agreements"]);

    public async Task<AgentExecutionResult> StartAsync(
        ConversationThread conversation,
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
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (operation.Status == AgentOperationStatus.ClarificationRequired && attachments.Count > 0)
        {
            return StartAsync(conversation, userMessage, operation, attachments, context, cancellationToken);
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
