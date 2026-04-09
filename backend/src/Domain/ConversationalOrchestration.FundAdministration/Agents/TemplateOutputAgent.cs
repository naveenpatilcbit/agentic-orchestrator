using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Agents;
using ConversationalOrchestration.Domain.Auditing;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using ConversationalOrchestration.FundAdministration.CapitalCalls;

namespace ConversationalOrchestration.FundAdministration.Agents;

public sealed class TemplateOutputAgent : IAgent
{
    private static readonly IReadOnlyCollection<AgentInputFieldDefinition> InputFields =
    [
        new("sourceOperationId", "source operation id", "Approved operation id whose reviewed data should be used for the template output.", true),
        new("templateName", "template name", "Optional target template or workbook name to use for the generated output.", false, "Capital Call LP Notice Workbook")
    ];

    private readonly IAgentInputCompletionService _inputCompletionService;
    private readonly ITemplateOutputGenerationService _templateOutputGenerationService;

    public TemplateOutputAgent(
        IAgentInputCompletionService inputCompletionService,
        ITemplateOutputGenerationService templateOutputGenerationService)
    {
        _inputCompletionService = inputCompletionService;
        _templateOutputGenerationService = templateOutputGenerationService;
    }

    public AgentDefinition Definition { get; } = new(
        FundAdministrationAgentIds.TemplateOutput,
        "Template Output Agent",
        "Takes approved workflow data from another operation and renders it into a reusable output template.",
        AgentExecutionMode.InlineFunction);

    public Task<AgentExecutionResult> StartAsync(
        ConversationThread conversation,
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken) =>
        ExecuteAsync(conversationHistory, userMessage, operation, attachments, context, cancellationToken);

    public Task<AgentExecutionResult> ContinueAsync(
        ConversationThread conversation,
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken) =>
        ExecuteAsync(conversationHistory, userMessage, operation, attachments, context, cancellationToken);

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
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var payload = await CompleteInputsAsync(conversationHistory, userMessage, operation, attachments, cancellationToken);
        AgentInputStateSupport.MergeIfPresent(payload, "templateName", attachments.FirstOrDefault()?.FileName);

        var missingRequiredFields = AgentInputStateSupport.GetMissingRequiredFieldNames(InputFields, payload);
        var templateName = AgentInputStateSupport.GetValue(payload, "templateName");
        operation.Title = string.IsNullOrWhiteSpace(templateName)
            ? "Template Output"
            : $"{templateName} Output";

        if (missingRequiredFields.Count > 0)
        {
            operation.Status = AgentOperationStatus.ClarificationRequired;
            operation.CurrentStep = "CollectTemplateOutputInputs";
            operation.PendingClarification = AgentInputStateSupport.BuildPendingClarification(InputFields, missingRequiredFields, "generate the template output");
            operation.Summary = "Waiting for the source operation that contains approved data.";
            operation.DataJson = AgentInputStateSupport.SerializeValues(payload);

            return new AgentExecutionResult(
                AgentInputStateSupport.BuildAssistantClarificationMessage(InputFields, missingRequiredFields, "template output generation"),
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.AskForMoreInfo,
                    Label = "Provide template output inputs",
                    PayloadJson = JsonContent.Serialize(new { required = missingRequiredFields })
                }],
                BuildAudit(operation, "TemplateOutputClarificationRequested", payload));
        }

        try
        {
            var result = await _templateOutputGenerationService.GenerateAsync(
                context.TenantId,
                operation.ConversationId,
                payload["sourceOperationId"]!,
                templateName,
                cancellationToken);

            payload["fileAssetId"] = result.FileAssetId;
            payload["downloadRoute"] = result.DownloadRoute;
            payload["generatedFileName"] = result.FileName;

            operation.Status = AgentOperationStatus.Completed;
            operation.CurrentStep = "ArtifactReady";
            operation.PendingClarification = null;
            operation.Summary = result.Summary;
            operation.DataJson = AgentInputStateSupport.SerializeValues(payload);

            return new AgentExecutionResult(
                result.Summary,
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.DownloadArtifact,
                    Label = "Download template output",
                    Route = result.DownloadRoute,
                    PayloadJson = JsonContent.Serialize(payload)
                }],
                BuildAudit(operation, "TemplateOutputGenerated", payload));
        }
        catch (InvalidOperationException exception)
        {
            operation.Status = AgentOperationStatus.Failed;
            operation.CurrentStep = "TemplateOutputFailed";
            operation.PendingClarification = null;
            operation.Summary = exception.Message;
            operation.DataJson = AgentInputStateSupport.SerializeValues(payload);

            return new AgentExecutionResult(
                $"I couldn't generate the template output yet: {exception.Message}",
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.ShowStatus,
                    Label = "Review template output status",
                    PayloadJson = JsonContent.Serialize(new { operationId = operation.Id, status = operation.Status.ToString() })
                }],
                BuildAudit(operation, "TemplateOutputFailed", new Dictionary<string, string?>(payload, StringComparer.OrdinalIgnoreCase)
                {
                    ["error"] = exception.Message
                }));
        }
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

    private static IReadOnlyCollection<AuditEvent> BuildAudit(AgentOperation operation, string eventType, IDictionary<string, string?> payload) =>
    [
        new AuditEvent
        {
            TenantId = operation.TenantId,
            ConversationId = operation.ConversationId,
            OperationId = operation.Id,
            EventType = eventType,
            ActorType = "Agent",
            ActorId = FundAdministrationAgentIds.TemplateOutput,
            DataJson = JsonContent.Serialize(payload)
        }
    ];
}
