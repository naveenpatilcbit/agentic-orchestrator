using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Agents;
using ConversationalOrchestration.Domain.Auditing;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using ConversationalOrchestration.FundAdministration.CapitalCalls;
using ConversationalOrchestration.FundAdministration.Workflows;

namespace ConversationalOrchestration.FundAdministration.Agents;

public sealed class NoticeCreationAgent : IAgent
{
    private readonly IFundAdministrationWorkflowDispatcher _workflowDispatcher;

    public NoticeCreationAgent(IFundAdministrationWorkflowDispatcher workflowDispatcher)
    {
        _workflowDispatcher = workflowDispatcher;
    }

    public AgentDefinition Definition { get; } = new(
        FundAdministrationAgentIds.NoticeCreation,
        "Notice Creation Helper",
        "Calculates partner and feeder allocations for a capital call notice, then materializes a draft or downloadable output.",
        AgentExecutionMode.Workflow);

    public async Task<AgentExecutionResult> StartAsync(
        ConversationThread conversation,
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        operation.Title = "Capital Call Notice";
        operation.Status = AgentOperationStatus.Running;
        operation.CurrentStep = "CapitalCallWorkflow";
        operation.PendingClarification = null;
        operation.ActiveReviewTaskId = null;
        operation.Summary = "Preparing the capital call allocation workflow.";

        await _workflowDispatcher.StartCapitalCallNoticeAsync(
            operation,
            conversation,
            userMessage.Content,
            context,
            cancellationToken);

        return BuildResult(operation);
    }

    public async Task<AgentExecutionResult> ContinueAsync(
        ConversationThread conversation,
        IReadOnlyCollection<ConversationMessage> conversationHistory,
        ConversationMessage userMessage,
        AgentOperation operation,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (operation.Status != AgentOperationStatus.ClarificationRequired)
        {
            return new AgentExecutionResult(
                "The capital call workflow is already running or completed. Ask for the status if you want a quick summary of the latest state.",
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.ShowStatus,
                    Label = "View capital call status",
                    PayloadJson = JsonContent.Serialize(new { operationId = operation.Id, status = operation.Status.ToString() })
                }]);
        }

        await _workflowDispatcher.ContinueCapitalCallNoticeAsync(
            operation,
            conversation,
            userMessage.Content,
            context,
            cancellationToken);

        return BuildResult(operation);
    }

    public Task<string> DescribeStatusAsync(
        AgentOperation operation,
        IReadOnlyCollection<ReviewTask> relatedTasks,
        CancellationToken cancellationToken)
    {
        if (operation.Status == AgentOperationStatus.ClarificationRequired)
        {
            return Task.FromResult(operation.PendingClarification ?? "Waiting for additional capital call details.");
        }

        return Task.FromResult(string.IsNullOrWhiteSpace(operation.Summary)
            ? "The capital call workflow is active."
            : operation.Summary);
    }

    private AgentExecutionResult BuildResult(AgentOperation operation)
    {
        var data = JsonContent.Deserialize<CapitalCallOperationData>(operation.DataJson);

        return operation.Status switch
        {
            AgentOperationStatus.ClarificationRequired => new AgentExecutionResult(
                operation.PendingClarification ?? "I need a few more details to calculate the capital call notice.",
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.AskForMoreInfo,
                    Label = "Provide clarification",
                    PayloadJson = JsonContent.Serialize(new { operationId = operation.Id, step = operation.CurrentStep })
                }],
                BuildAudit(operation, "CapitalCallClarificationRequested", new { operation.PendingClarification })),

            AgentOperationStatus.Completed when data is not null && !string.IsNullOrWhiteSpace(data.DraftRoute) => new AgentExecutionResult(
                $"I calculated the partner allocations for {data.FundName} and created a draft capital call notice in {data.RootCurrency}.",
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.OpenPageWithPrefill,
                    Label = "Open draft notice",
                    Route = data.DraftRoute,
                    PayloadJson = JsonContent.Serialize(data)
                }],
                BuildAudit(operation, "CapitalCallDraftMaterialized", data)),

            AgentOperationStatus.Completed when data is not null && !string.IsNullOrWhiteSpace(data.DownloadRoute) => new AgentExecutionResult(
                $"I calculated the partner allocations for {data.FundName} and generated a downloadable output file.",
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.DownloadArtifact,
                    Label = "Download allocation output",
                    Route = data.DownloadRoute,
                    PayloadJson = JsonContent.Serialize(data)
                }],
                BuildAudit(operation, "CapitalCallArtifactMaterialized", data)),

            AgentOperationStatus.Failed => new AgentExecutionResult(
                $"The capital call workflow hit an error: {operation.Summary}",
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.ShowStatus,
                    Label = "Review failure details",
                    PayloadJson = JsonContent.Serialize(new { operationId = operation.Id, status = operation.Status.ToString() })
                }],
                BuildAudit(operation, "CapitalCallWorkflowFailed", new { operation.Summary })),

            _ => new AgentExecutionResult(
                operation.Summary,
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.StartAsyncOperation,
                    Label = "Track capital call workflow",
                    PayloadJson = JsonContent.Serialize(new { operationId = operation.Id, status = operation.Status.ToString() })
                }],
                BuildAudit(operation, "CapitalCallWorkflowProgressed", new { operation.Status, operation.CurrentStep }))
        };
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
            ActorId = FundAdministrationAgentIds.NoticeCreation,
            DataJson = JsonContent.Serialize(payload)
        }
    ];
}
