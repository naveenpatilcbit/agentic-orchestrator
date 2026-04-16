using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Agents;
using ConversationalOrchestration.Domain.Auditing;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;

namespace ConversationalOrchestration.FundAdministration.Agents;

public sealed class OnePagerAgent : IAgent
{
    private static readonly IReadOnlyCollection<AgentInputFieldDefinition> StartupFields =
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
        FundAdministrationAgentIds.OnePager,
        "One Pager Generation Agent",
        "Builds a one-pager draft using internal portfolio data and the selected format.",
        AgentExecutionMode.InlineFunction,
        new AgentStartRequirements(
            StartupFields,
            Guidance: "Try to collect the company name before starting. The format can come from the user or an uploaded template.")); 

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
        var inputFields = Definition.StartRequirements?.Fields ?? Array.Empty<AgentInputFieldDefinition>();
        var missingRequiredFields = AgentInputStateSupport.GetMissingRequiredFieldNames(inputFields, payload);

        operation.Title = string.IsNullOrWhiteSpace(companyName)
            ? "One Pager Draft"
            : $"{companyName} One Pager";

        if (missingRequiredFields.Count > 0)
        {
            operation.Status = AgentOperationStatus.ClarificationRequired;
            operation.CurrentStep = "CollectCompany";
            operation.PendingClarification = AgentInputStateSupport.BuildPendingClarification(inputFields, missingRequiredFields, "generate the one-pager");
            operation.Summary = "Waiting for the target company.";
            operation.DataJson = AgentInputStateSupport.SerializeValues(payload);

            return new AgentExecutionResult(
                AgentInputStateSupport.BuildAssistantClarificationMessage(inputFields, missingRequiredFields, "one-pager generation"),
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
                Definition.StartRequirements?.Fields ?? Array.Empty<AgentInputFieldDefinition>()),
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
            ActorId = FundAdministrationAgentIds.OnePager,
            DataJson = JsonContent.Serialize(payload)
        }
    ];
}
