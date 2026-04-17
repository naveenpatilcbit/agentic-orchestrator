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
    private static readonly IReadOnlyCollection<AgentInputFieldDefinition> StartupFields =
    [
        new("fundName", "fund name", "Name of the fund for which the capital call notice should be created.", true, "Apex Fund I"),
        new("capitalCallAmount", "capital call amount", "Total amount to raise for the notice.", true, "100000"),
        new("noticeDate", "notice date", "Optional effective or notice date if the user already knows it.", false, "2026-04-17")
    ];

    private readonly IFundAdministrationWorkflowDispatcher _workflowDispatcher;

    public NoticeCreationAgent(IFundAdministrationWorkflowDispatcher workflowDispatcher)
    {
        _workflowDispatcher = workflowDispatcher;
    }

    public AgentDefinition Definition { get; } = new(
        FundAdministrationAgentIds.NoticeCreation,
        "Notice Creation Helper",
        "Extracts partner and feeder commitment data for a capital call notice, routes it through human review, then calculates approved allocations for downstream template output.",
        AgentExecutionMode.Workflow,
        new AgentStartRequirements(
            StartupFields,
            Guidance: "Try to gather the fund name and capital call amount up front. If the source data is not already available in the configured provider, the agent may still need a CSV upload.")); 

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

        if (operation.Status == AgentOperationStatus.WaitingForHumanReview)
        {
            return Task.FromResult(string.IsNullOrWhiteSpace(operation.Summary)
                ? "Extracted capital call partner data is waiting for human review."
                : operation.Summary);
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

            AgentOperationStatus.Completed when data?.Notice is not null => new AgentExecutionResult(
                $"I applied the reviewed partner data for {data.FundName}, calculated the final allocations, and captured the final confirmation. The approved data is ready, and template rendering is now the current step.",
                operation,
                BuildCompletedActions(operation, data),
                BuildAudit(operation, "CapitalCallReviewedDataReady", data)),

            AgentOperationStatus.WaitingForHumanReview => new AgentExecutionResult(
                string.IsNullOrWhiteSpace(operation.Summary)
                    ? "Extracted partner and commitment data is ready for review before the allocation engine continues."
                    : operation.Summary,
                operation,
                [new AgentAction
                {
                    Type = AgentActionType.ShowStatus,
                    Label = "Open review queue",
                    PayloadJson = JsonContent.Serialize(new { operationId = operation.Id, status = operation.Status.ToString() })
                }],
                BuildAudit(operation, "CapitalCallReviewRequested", new { operation.Status, operation.CurrentStep })),

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

    private static IReadOnlyCollection<AgentAction> BuildCompletedActions(AgentOperation operation, CapitalCallOperationData data)
    {
        var actions = new List<AgentAction>();

        if (!string.IsNullOrWhiteSpace(data.ReviewDownloadRoute))
        {
            actions.Add(new AgentAction
            {
                Type = AgentActionType.DownloadArtifact,
                Label = "Download reviewed workbook",
                Route = data.ReviewDownloadRoute,
                PayloadJson = JsonContent.Serialize(data)
            });
        }

        actions.Add(new AgentAction
        {
            Type = AgentActionType.StartAsyncOperation,
            Label = "Generate template output",
            PayloadJson = JsonContent.Serialize(new
            {
                sourceOperationId = operation.Id,
                startMessage = $"Generate template output for approved capital call operation {operation.Id}"
            })
        });

        return actions;
    }
}
