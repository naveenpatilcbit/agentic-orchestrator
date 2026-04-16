using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Agents;
using ConversationalOrchestration.Domain.Auditing;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using ConversationalOrchestration.FundAdministration.Workflows;

namespace ConversationalOrchestration.FundAdministration.Agents;

public sealed class FundOnboardingAgent : IAgent
{
    private static readonly IReadOnlyCollection<AgentInputFieldDefinition> StartupFields = [];

    private readonly IFundAdministrationWorkflowDispatcher _workflowDispatcher;

    public FundOnboardingAgent(IFundAdministrationWorkflowDispatcher workflowDispatcher)
    {
        _workflowDispatcher = workflowDispatcher;
    }

    public AgentDefinition Definition { get; } = new(
        FundAdministrationAgentIds.FundOnboarding,
        "Fund Onboarding Helper",
        "Starts the onboarding workflow, waits at review checkpoints, and resumes from stored workflow state.",
        AgentExecutionMode.Workflow,
        new AgentStartRequirements(
            StartupFields,
            AllowsPartialStart: false,
            RequiresAttachment: true,
            Guidance: "This workflow expects uploaded fund agreements or onboarding documents before it can start cleanly.")); 

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

        await _workflowDispatcher.StartOnboardingAsync(
            operation,
            conversation,
            attachments,
            context,
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
            ActorId = FundAdministrationAgentIds.FundOnboarding,
            DataJson = JsonContent.Serialize(payload)
        }
    ];
}
