using FundOrchestrator.Application.Abstractions;
using FundOrchestrator.Application.Support;
using FundOrchestrator.Contracts.Messaging;
using FundOrchestrator.Domain.Auditing;
using FundOrchestrator.Domain.Conversations;
using FundOrchestrator.Domain.Operations;
using FundOrchestrator.Domain.Reviews;
using NServiceBus;

namespace FundOrchestrator.Worker.Sagas;

public sealed class OnboardingSaga :
    Saga<OnboardingSagaData>,
    IAmStartedByMessages<StartOnboardingWorkflowCommand>,
    IHandleMessages<AdvanceOnboardingReviewCommand>,
    IHandleTimeouts<ClassificationReadyTimeout>,
    IHandleTimeouts<ExtractionReadyTimeout>
{
    private readonly IAgentOperationRepository _operationRepository;
    private readonly IReviewTaskRepository _reviewTaskRepository;
    private readonly IConversationMessageRepository _conversationMessageRepository;
    private readonly IAuditEventRepository _auditEventRepository;

    public OnboardingSaga(
        IAgentOperationRepository operationRepository,
        IReviewTaskRepository reviewTaskRepository,
        IConversationMessageRepository conversationMessageRepository,
        IAuditEventRepository auditEventRepository)
    {
        _operationRepository = operationRepository;
        _reviewTaskRepository = reviewTaskRepository;
        _conversationMessageRepository = conversationMessageRepository;
        _auditEventRepository = auditEventRepository;
    }

    protected override void ConfigureHowToFindSaga(SagaPropertyMapper<OnboardingSagaData> mapper)
    {
        mapper.MapSaga(saga => saga.OperationId)
            .ToMessage<StartOnboardingWorkflowCommand>(message => message.OperationId)
            .ToMessage<AdvanceOnboardingReviewCommand>(message => message.OperationId);
    }

    public async Task Handle(StartOnboardingWorkflowCommand message, IMessageHandlerContext context)
    {
        Data.OperationId = message.OperationId;
        Data.ConversationId = message.ConversationId;
        Data.TenantId = message.TenantId;

        await _auditEventRepository.AddAsync(
            new AuditEvent
            {
                TenantId = message.TenantId,
                ConversationId = message.ConversationId,
                OperationId = message.OperationId,
                EventType = "OnboardingSagaStarted",
                ActorType = "Workflow",
                ActorId = nameof(OnboardingSaga),
                DataJson = JsonContent.Serialize(new { attachments = message.AttachmentIds })
            },
            context.CancellationToken);

        await RequestTimeout(context, TimeSpan.FromSeconds(6), new ClassificationReadyTimeout());
    }

    public async Task Timeout(ClassificationReadyTimeout state, IMessageHandlerContext context)
    {
        if (Data.ClassificationApproved)
        {
            return;
        }

        var operation = await _operationRepository.GetAsync(Data.OperationId, Data.TenantId, context.CancellationToken);
        if (operation is null)
        {
            MarkAsComplete();
            return;
        }

        var reviewTask = new ReviewTask
        {
            TenantId = Data.TenantId,
            ConversationId = Data.ConversationId,
            OperationId = Data.OperationId,
            Title = "Classification Review",
            TaskType = "ClassificationReview",
            ProposedPayloadJson = JsonContent.Serialize(new
            {
                documents = new[]
                {
                    new { name = "LPA.pdf", predictedType = "Limited Partnership Agreement" },
                    new { name = "Subscription.pdf", predictedType = "Subscription Document" }
                }
            })
        };

        operation.Status = AgentOperationStatus.WaitingForHumanReview;
        operation.CurrentStep = "ClassificationReview";
        operation.ActiveReviewTaskId = reviewTask.Id;
        operation.Summary = "Document classification is ready for human review.";
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _reviewTaskRepository.UpsertAsync(reviewTask, context.CancellationToken);
        await _operationRepository.UpsertAsync(operation, context.CancellationToken);
        await AddConversationUpdateAsync(operation, "Document classification review is ready. Approve it from the review queue to continue.");
        await _auditEventRepository.AddAsync(
            new AuditEvent
            {
                TenantId = Data.TenantId,
                ConversationId = Data.ConversationId,
                OperationId = Data.OperationId,
                EventType = "ClassificationReviewCreated",
                ActorType = "Workflow",
                ActorId = nameof(OnboardingSaga)
            },
            context.CancellationToken);
    }

    public async Task Handle(AdvanceOnboardingReviewCommand message, IMessageHandlerContext context)
    {
        var operation = await _operationRepository.GetAsync(message.OperationId, message.TenantId, context.CancellationToken);
        if (operation is null)
        {
            MarkAsComplete();
            return;
        }

        if (message.ReviewType == "ClassificationReview" && !Data.ClassificationApproved)
        {
            Data.ClassificationApproved = true;
            operation.Status = AgentOperationStatus.WaitingForExternalSystem;
            operation.CurrentStep = "Extraction";
            operation.Summary = "Classification approved. Waiting for extraction results.";
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _operationRepository.UpsertAsync(operation, context.CancellationToken);
            await AddConversationUpdateAsync(operation, "Classification approved. I submitted the next extraction stage.");
            await RequestTimeout(context, TimeSpan.FromSeconds(6), new ExtractionReadyTimeout());
            return;
        }

        if (message.ReviewType == "ExtractionReview")
        {
            Data.ExtractionApproved = true;
            operation.Status = AgentOperationStatus.Completed;
            operation.CurrentStep = "DraftCreated";
            operation.ActiveReviewTaskId = null;
            operation.Summary = "Extraction approved and fund draft created.";
            operation.DataJson = JsonContent.Serialize(new
            {
                fundDraftRoute = $"/funds/drafts/{operation.Id}",
                extractedPayload = message.FinalPayloadJson
            });
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _operationRepository.UpsertAsync(operation, context.CancellationToken);
            await AddConversationUpdateAsync(operation, "Extraction approved. The fund draft is now ready for final review.");
            await _auditEventRepository.AddAsync(
                new AuditEvent
                {
                    TenantId = Data.TenantId,
                    ConversationId = Data.ConversationId,
                    OperationId = Data.OperationId,
                    EventType = "OnboardingCompleted",
                    ActorType = "Workflow",
                    ActorId = nameof(OnboardingSaga),
                    DataJson = message.FinalPayloadJson
                },
                context.CancellationToken);
            MarkAsComplete();
        }
    }

    public async Task Timeout(ExtractionReadyTimeout state, IMessageHandlerContext context)
    {
        if (Data.ExtractionApproved)
        {
            return;
        }

        var operation = await _operationRepository.GetAsync(Data.OperationId, Data.TenantId, context.CancellationToken);
        if (operation is null)
        {
            MarkAsComplete();
            return;
        }

        var reviewTask = new ReviewTask
        {
            TenantId = Data.TenantId,
            ConversationId = Data.ConversationId,
            OperationId = Data.OperationId,
            Title = "Extraction Review",
            TaskType = "ExtractionReview",
            ProposedPayloadJson = JsonContent.Serialize(new
            {
                fundName = "Apex Growth Fund II",
                managementFee = "1.75%",
                domicile = "Delaware",
                currency = "USD"
            })
        };

        operation.Status = AgentOperationStatus.WaitingForHumanReview;
        operation.CurrentStep = "ExtractionReview";
        operation.ActiveReviewTaskId = reviewTask.Id;
        operation.Summary = "Field extraction is ready for review.";
        operation.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _reviewTaskRepository.UpsertAsync(reviewTask, context.CancellationToken);
        await _operationRepository.UpsertAsync(operation, context.CancellationToken);
        await AddConversationUpdateAsync(operation, "Extraction review is ready. Approve the extracted fields to create the draft record.");
        await _auditEventRepository.AddAsync(
            new AuditEvent
            {
                TenantId = Data.TenantId,
                ConversationId = Data.ConversationId,
                OperationId = Data.OperationId,
                EventType = "ExtractionReviewCreated",
                ActorType = "Workflow",
                ActorId = nameof(OnboardingSaga)
            },
            context.CancellationToken);
    }

    private Task AddConversationUpdateAsync(AgentOperation operation, string content) =>
        _conversationMessageRepository.AddAsync(
            new ConversationMessage
            {
                TenantId = operation.TenantId,
                ConversationId = operation.ConversationId,
                OperationId = operation.Id,
                AuthorId = "workflow",
                Role = ConversationMessageRole.System,
                Content = content,
                MessageKind = "workflow"
            },
            CancellationToken.None);
}

public sealed class OnboardingSagaData : ContainSagaData
{
    public string OperationId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public bool ClassificationApproved { get; set; }
    public bool ExtractionApproved { get; set; }
}

public sealed class ClassificationReadyTimeout
{
}

public sealed class ExtractionReadyTimeout
{
}
