using FundOrchestrator.Application.Abstractions;
using FundOrchestrator.Application.Support;
using FundOrchestrator.Contracts;
using FundOrchestrator.Domain.Auditing;
using FundOrchestrator.Domain.Conversations;
using FundOrchestrator.Domain.Files;
using FundOrchestrator.Domain.Operations;
using FundOrchestrator.Domain.Reviews;

namespace FundOrchestrator.Application.Conversations;

public sealed class ChatOrchestratorService : IChatOrchestratorService
{
    private readonly IConversationRepository _conversationRepository;
    private readonly IConversationMessageRepository _conversationMessageRepository;
    private readonly IAgentOperationRepository _operationRepository;
    private readonly IReviewTaskRepository _reviewTaskRepository;
    private readonly IAuditEventRepository _auditEventRepository;
    private readonly IFileAssetRepository _fileAssetRepository;
    private readonly IMessageRoutingService _routingService;
    private readonly IAgentCatalog _agentCatalog;
    private readonly IReviewTaskService _reviewTaskService;

    public ChatOrchestratorService(
        IConversationRepository conversationRepository,
        IConversationMessageRepository conversationMessageRepository,
        IAgentOperationRepository operationRepository,
        IReviewTaskRepository reviewTaskRepository,
        IAuditEventRepository auditEventRepository,
        IFileAssetRepository fileAssetRepository,
        IMessageRoutingService routingService,
        IAgentCatalog agentCatalog,
        IReviewTaskService reviewTaskService)
    {
        _conversationRepository = conversationRepository;
        _conversationMessageRepository = conversationMessageRepository;
        _operationRepository = operationRepository;
        _reviewTaskRepository = reviewTaskRepository;
        _auditEventRepository = auditEventRepository;
        _fileAssetRepository = fileAssetRepository;
        _routingService = routingService;
        _agentCatalog = agentCatalog;
        _reviewTaskService = reviewTaskService;
    }

    public async Task<ConversationSnapshotResponse> HandleMessageAsync(
        ChatMessageRequest request,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var conversation = await GetOrCreateConversationAsync(request.ConversationId, request.Message, context, cancellationToken);
        var attachments = await LoadAttachmentsAsync(request.AttachmentIds, context, cancellationToken);

        var userMessage = new ConversationMessage
        {
            TenantId = context.TenantId,
            ConversationId = conversation.Id,
            AuthorId = context.UserId,
            Role = ConversationMessageRole.User,
            Content = request.Message
        };

        await _conversationMessageRepository.AddAsync(userMessage, cancellationToken);

        var operations = await _operationRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);
        var reviewTasks = await _reviewTaskRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);
        var routingDecision = await _routingService.DecideAsync(request.Message, conversation, operations, reviewTasks, attachments, cancellationToken);

        switch (routingDecision.Type)
        {
            case RoutingDecisionType.StartNewOperation:
                await HandleStartNewOperationAsync(conversation, userMessage, routingDecision, attachments, context, cancellationToken);
                break;
            case RoutingDecisionType.ContinueOperation:
                await HandleContinueOperationAsync(conversation, userMessage, routingDecision, attachments, context, cancellationToken);
                break;
            case RoutingDecisionType.RespondToReviewTask:
                await HandleReviewResponseAsync(routingDecision, request.Message, context, cancellationToken);
                break;
            case RoutingDecisionType.AskStatus:
                await HandleStatusRequestAsync(conversation, routingDecision, context, cancellationToken);
                break;
            default:
                await AddAssistantMessageAsync(
                    conversation.Id,
                    context,
                    "I can help, but I need a little more direction because there are multiple active threads in this conversation. Try naming the workflow you want to continue, or ask me to start a new one explicitly.",
                    null,
                    "routing");
                break;
        }

        return (await GetSnapshotAsync(conversation.Id, context, cancellationToken))!;
    }

    public async Task<ConversationSnapshotResponse?> GetSnapshotAsync(
        string conversationId,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversationRepository.GetAsync(conversationId, context.TenantId, cancellationToken);
        if (conversation is null)
        {
            return null;
        }

        var messages = await _conversationMessageRepository.ListByConversationAsync(conversationId, context.TenantId, cancellationToken);
        var operations = await _operationRepository.ListByConversationAsync(conversationId, context.TenantId, cancellationToken);
        var reviewTasks = await _reviewTaskRepository.ListByConversationAsync(conversationId, context.TenantId, cancellationToken);
        var files = await _fileAssetRepository.ListByConversationAsync(conversationId, context.TenantId, cancellationToken);

        return new ConversationSnapshotResponse(
            conversation.Id,
            conversation.Title,
            messages.OrderBy(message => message.CreatedAtUtc).Select(MapMessage).ToArray(),
            operations.OrderByDescending(operation => operation.UpdatedAtUtc).Select(MapOperation).ToArray(),
            reviewTasks.OrderByDescending(task => task.UpdatedAtUtc).Select(MapReviewTask).ToArray(),
            files.OrderByDescending(file => file.UploadedAtUtc).Select(MapFile).ToArray());
    }

    public async Task<IReadOnlyCollection<ConversationSummaryDto>> ListConversationsAsync(
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var conversations = await _conversationRepository.ListByTenantAsync(context.TenantId, cancellationToken);
        if (conversations.Count == 0)
        {
            return Array.Empty<ConversationSummaryDto>();
        }

        var summaries = new List<ConversationSummaryDto>(conversations.Count);

        foreach (var conversation in conversations.OrderByDescending(item => item.UpdatedAtUtc))
        {
            var messages = await _conversationMessageRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);
            var operations = await _operationRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);
            var reviewTasks = await _reviewTaskRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);

            summaries.Add(new ConversationSummaryDto(
                conversation.Id,
                conversation.Title,
                messages.Count,
                operations.Count(operation => operation.Status is not AgentOperationStatus.Completed and not AgentOperationStatus.Failed and not AgentOperationStatus.Cancelled),
                reviewTasks.Count(task => task.Status == ReviewTaskStatus.Open),
                conversation.UpdatedAtUtc));
        }

        return summaries;
    }

    private async Task HandleStartNewOperationAsync(
        ConversationThread conversation,
        ConversationMessage userMessage,
        RoutingDecision routingDecision,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var agent = _agentCatalog.Resolve(routingDecision.AgentId!);
        var operation = new AgentOperation
        {
            TenantId = context.TenantId,
            ConversationId = conversation.Id,
            AgentId = agent.Definition.Id,
            Title = agent.Definition.DisplayName,
            CreatedByUserId = context.UserId,
            Status = AgentOperationStatus.Received
        };

        await _operationRepository.UpsertAsync(operation, cancellationToken);
        var result = await agent.StartAsync(conversation, userMessage, operation, attachments, context, cancellationToken);
        await PersistAgentResultAsync(conversation, result, context, cancellationToken);
    }

    private async Task HandleContinueOperationAsync(
        ConversationThread conversation,
        ConversationMessage userMessage,
        RoutingDecision routingDecision,
        IReadOnlyCollection<FileAsset> attachments,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var operation = await _operationRepository.GetAsync(routingDecision.OperationId!, context.TenantId, cancellationToken);
        if (operation is null)
        {
            await AddAssistantMessageAsync(conversation.Id, context, "I couldn't find that operation anymore, so please restate the request and I'll start a fresh one.", null, "routing");
            return;
        }

        var agent = _agentCatalog.Resolve(operation.AgentId);
        var result = await agent.ContinueAsync(conversation, userMessage, operation, attachments, context, cancellationToken);
        await PersistAgentResultAsync(conversation, result, context, cancellationToken);
    }

    private async Task HandleReviewResponseAsync(
        RoutingDecision routingDecision,
        string message,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var result = await _reviewTaskService.HandleChatDecisionAsync(routingDecision, message, context, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.ConversationId))
        {
            await AddAssistantMessageAsync(result.ConversationId, context, result.AssistantMessage, result.OperationId, "review");
        }
    }

    private async Task HandleStatusRequestAsync(
        ConversationThread conversation,
        RoutingDecision routingDecision,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var operations = await _operationRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);
        var reviewTasks = await _reviewTaskRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);

        string response;
        string? operationId = null;

        if (!string.IsNullOrWhiteSpace(routingDecision.OperationId))
        {
            var operation = operations.FirstOrDefault(candidate => candidate.Id == routingDecision.OperationId);
            if (operation is null)
            {
                response = "I couldn't find that operation anymore.";
            }
            else
            {
                operationId = operation.Id;
                response = await _agentCatalog.Resolve(operation.AgentId)
                    .DescribeStatusAsync(operation, reviewTasks.Where(task => task.OperationId == operation.Id).ToArray(), cancellationToken);
            }
        }
        else
        {
            var active = operations
                .Where(static operation => operation.Status is not AgentOperationStatus.Completed and not AgentOperationStatus.Failed and not AgentOperationStatus.Cancelled)
                .OrderByDescending(operation => operation.UpdatedAtUtc)
                .ToArray();

            response = active.Length == 0
                ? "There are no active operations in this conversation right now."
                : $"Here's the active work summary:\n- {string.Join("\n- ", active.Select(operation => $"{operation.Title}: {operation.Status} ({operation.CurrentStep})"))}";
        }

        await AddAssistantMessageAsync(conversation.Id, context, response, operationId, "status");
    }

    private async Task PersistAgentResultAsync(
        ConversationThread conversation,
        AgentExecutionResult result,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        result.Operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _operationRepository.UpsertAsync(result.Operation, cancellationToken);

        conversation.LastFocusedOperationId = result.Operation.Id;
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _conversationRepository.UpsertAsync(conversation, cancellationToken);

        await AddAssistantMessageAsync(
            conversation.Id,
            context,
            result.AssistantMessage,
            result.Operation.Id,
            "chat",
            result.Actions);

        if (!string.IsNullOrWhiteSpace(result.FollowUpSystemMessage))
        {
            await AddAssistantMessageAsync(conversation.Id, context, result.FollowUpSystemMessage, result.Operation.Id, "status");
        }

        if (result.AuditEvents is not null)
        {
            foreach (var auditEvent in result.AuditEvents)
            {
                await _auditEventRepository.AddAsync(auditEvent, cancellationToken);
            }
        }
    }

    private async Task<ConversationThread> GetOrCreateConversationAsync(
        string? requestedConversationId,
        string openingMessage,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedConversationId))
        {
            var existing = await _conversationRepository.GetAsync(requestedConversationId, context.TenantId, cancellationToken);
            if (existing is not null)
            {
                existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await _conversationRepository.UpsertAsync(existing, cancellationToken);
                return existing;
            }
        }

        var conversation = new ConversationThread
        {
            Id = string.IsNullOrWhiteSpace(requestedConversationId) ? Guid.NewGuid().ToString("N") : requestedConversationId,
            TenantId = context.TenantId,
            Title = openingMessage.Length <= 56 ? openingMessage : $"{openingMessage[..56]}..."
        };

        await _conversationRepository.UpsertAsync(conversation, cancellationToken);
        return conversation;
    }

    private async Task<IReadOnlyCollection<FileAsset>> LoadAttachmentsAsync(
        IReadOnlyCollection<string>? attachmentIds,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (attachmentIds is null || attachmentIds.Count == 0)
        {
            return Array.Empty<FileAsset>();
        }

        return await _fileAssetRepository.ListByIdsAsync(attachmentIds, context.TenantId, cancellationToken);
    }

    private async Task AddAssistantMessageAsync(
        string conversationId,
        TenantExecutionContext context,
        string content,
        string? operationId,
        string messageKind,
        IReadOnlyCollection<AgentAction>? actions = null)
    {
        await _conversationMessageRepository.AddAsync(
            new ConversationMessage
            {
                TenantId = context.TenantId,
                ConversationId = conversationId,
                OperationId = operationId,
                AuthorId = "system",
                Role = ConversationMessageRole.Assistant,
                Content = content,
                MessageKind = messageKind,
                ActionsJson = actions is null ? null : JsonContent.Serialize(actions)
            },
            CancellationToken.None);
    }

    private static ConversationMessageDto MapMessage(ConversationMessage message)
    {
        var actions = JsonContent.Deserialize<IReadOnlyCollection<AgentAction>>(message.ActionsJson) ?? Array.Empty<AgentAction>();
        return new ConversationMessageDto(
            message.Id,
            message.ConversationId,
            message.OperationId,
            message.Role.ToString(),
            message.Content,
            message.MessageKind,
            actions.Select(action => new AgentActionDto(action.Type.ToString(), action.Label, action.Route, action.PayloadJson)).ToArray(),
            message.CreatedAtUtc);
    }

    private static AgentOperationDto MapOperation(AgentOperation operation) =>
        new(
            operation.Id,
            operation.AgentId,
            operation.Title,
            operation.Status.ToString(),
            operation.CurrentStep,
            operation.Summary,
            operation.PendingClarification,
            operation.ActiveReviewTaskId,
            operation.UpdatedAtUtc);

    private static ReviewTaskDto MapReviewTask(ReviewTask task) =>
        new(
            task.Id,
            task.OperationId,
            task.Title,
            task.TaskType,
            task.Status.ToString(),
            task.ProposedPayloadJson,
            task.FinalPayloadJson,
            task.Notes,
            task.UpdatedAtUtc);

    private static FileAssetDto MapFile(FileAsset asset) =>
        new(
            asset.Id,
            asset.ConversationId,
            asset.FileName,
            asset.ContentType,
            asset.SizeBytes,
            asset.UploadedAtUtc);
}
