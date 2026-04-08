using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Agents;
using ConversationalOrchestration.Domain.Auditing;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;

namespace ConversationalOrchestration.FundAdministration.Agents;

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
        FundAdministrationAgentIds.NoticeCreation,
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
            ActorId = FundAdministrationAgentIds.NoticeCreation,
            DataJson = JsonContent.Serialize(payload)
        }
    ];
}
