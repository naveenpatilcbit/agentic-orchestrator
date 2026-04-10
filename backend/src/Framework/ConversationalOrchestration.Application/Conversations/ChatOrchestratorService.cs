using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Contracts;
using ConversationalOrchestration.Domain.Auditing;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using Microsoft.Extensions.Logging;

namespace ConversationalOrchestration.Application.Conversations;

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
    private readonly IConversationHistoryCompactionService _conversationHistoryCompactionService;
    private readonly ILogger<ChatOrchestratorService> _logger;

    public ChatOrchestratorService(
        IConversationRepository conversationRepository,
        IConversationMessageRepository conversationMessageRepository,
        IAgentOperationRepository operationRepository,
        IReviewTaskRepository reviewTaskRepository,
        IAuditEventRepository auditEventRepository,
        IFileAssetRepository fileAssetRepository,
        IMessageRoutingService routingService,
        IAgentCatalog agentCatalog,
        IReviewTaskService reviewTaskService,
        IConversationHistoryCompactionService conversationHistoryCompactionService,
        ILogger<ChatOrchestratorService> logger)
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
        _conversationHistoryCompactionService = conversationHistoryCompactionService;
        _logger = logger;
    }

    public async Task<ConversationSnapshotResponse> HandleMessageAsync(
        ChatMessageRequest request,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var conversation = await GetOrCreateConversationAsync(request.ConversationId, request.Message, context, cancellationToken);
        var attachments = await LoadAttachmentsAsync(request.AttachmentIds, context, cancellationToken);
        _logger.LogInformation(
            "Handling chat message for tenant {TenantId} conversation {ConversationId}. RequestedConversationId={RequestedConversationId} Attachments={AttachmentCount} MessageLength={MessageLength}",
            context.TenantId,
            conversation.Id,
            request.ConversationId,
            attachments.Count,
            request.Message.Length);

        var userMessage = new ConversationMessage
        {
            TenantId = context.TenantId,
            ConversationId = conversation.Id,
            AuthorId = context.UserId,
            Role = ConversationMessageRole.User,
            Content = request.Message
        };

        await _conversationMessageRepository.AddAsync(userMessage, cancellationToken);
        await _conversationHistoryCompactionService.RefreshAsync(context.TenantId, conversation.Id, cancellationToken);

        // A single conversation can carry multiple operations at once, so routing always looks at
        // active operations and review tasks before deciding whether this message is new work.
        var operations = await _operationRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);
        var reviewTasks = await _reviewTaskRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);
        var routingDecision = await _routingService.DecideAsync(request.Message, conversation, operations, reviewTasks, attachments, cancellationToken);
        _logger.LogInformation(
            "Routing decision {DecisionType} selected for tenant {TenantId} conversation {ConversationId}. AgentId={AgentId} OperationId={OperationId} ReviewTaskId={ReviewTaskId} Explanation={Explanation}",
            routingDecision.Type,
            context.TenantId,
            conversation.Id,
            routingDecision.AgentId,
            routingDecision.OperationId,
            routingDecision.ReviewTaskId,
            routingDecision.Explanation);

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

    public async Task<ConversationSnapshotResponse> CreateConversationAsync(
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var conversation = new ConversationThread
        {
            TenantId = context.TenantId,
            Title = "New conversation"
        };

        await _conversationRepository.UpsertAsync(conversation, cancellationToken);

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
        _logger.LogInformation(
            "Starting new operation {OperationId} using agent {AgentId} for tenant {TenantId} conversation {ConversationId}.",
            operation.Id,
            agent.Definition.Id,
            context.TenantId,
            conversation.Id);
        conversation.LastFocusedOperationId = operation.Id;
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _conversationRepository.UpsertAsync(conversation, cancellationToken);
        var conversationHistory = await AttachOperationToUserMessageAndLoadHistoryAsync(userMessage, operation.Id, context, cancellationToken);
        var result = await agent.StartAsync(conversation, conversationHistory, userMessage, operation, attachments, context, cancellationToken);
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
            _logger.LogWarning(
                "Routing attempted to continue missing operation {OperationId} for tenant {TenantId} conversation {ConversationId}.",
                routingDecision.OperationId,
                context.TenantId,
                conversation.Id);
            await AddAssistantMessageAsync(conversation.Id, context, "I couldn't find that operation anymore, so please restate the request and I'll start a fresh one.", null, "routing");
            return;
        }

        var agent = _agentCatalog.Resolve(operation.AgentId);
        _logger.LogInformation(
            "Continuing operation {OperationId} using agent {AgentId} for tenant {TenantId} conversation {ConversationId}. Status={Status} Step={CurrentStep}",
            operation.Id,
            operation.AgentId,
            context.TenantId,
            conversation.Id,
            operation.Status,
            operation.CurrentStep);
        conversation.LastFocusedOperationId = operation.Id;
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _conversationRepository.UpsertAsync(conversation, cancellationToken);
        var conversationHistory = await AttachOperationToUserMessageAndLoadHistoryAsync(userMessage, operation.Id, context, cancellationToken);
        var result = await agent.ContinueAsync(conversation, conversationHistory, userMessage, operation, attachments, context, cancellationToken);
        await PersistAgentResultAsync(conversation, result, context, cancellationToken);
    }

    private async Task HandleReviewResponseAsync(
        RoutingDecision routingDecision,
        string message,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Handling review chat response for tenant {TenantId}. ReviewTaskId={ReviewTaskId} OperationId={OperationId}",
            context.TenantId,
            routingDecision.ReviewTaskId,
            routingDecision.OperationId);
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
        _logger.LogInformation(
            "Handling status request for tenant {TenantId} conversation {ConversationId}. OperationId={OperationId}",
            context.TenantId,
            conversation.Id,
            routingDecision.OperationId);
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
        _logger.LogInformation(
            "Persisted agent result for operation {OperationId} in tenant {TenantId} conversation {ConversationId}. Status={Status} Step={CurrentStep} Actions={ActionCount}",
            result.Operation.Id,
            context.TenantId,
            conversation.Id,
            result.Operation.Status,
            result.Operation.CurrentStep,
            result.Actions?.Count ?? 0);

        conversation.LastFocusedOperationId = result.Operation.Id;
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _conversationRepository.UpsertAsync(conversation, cancellationToken);

        // Assistant messages are appended after the operation is saved so polling clients always
        // see a conversation message that matches the latest persisted operation state.
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
                if (existing.Title == "New conversation")
                {
                    existing.Title = BuildConversationTitle(openingMessage);
                }

                existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await _conversationRepository.UpsertAsync(existing, cancellationToken);
                _logger.LogDebug(
                    "Resolved existing conversation {ConversationId} for tenant {TenantId}.",
                    existing.Id,
                    context.TenantId);
                return existing;
            }
        }

        var conversation = new ConversationThread
        {
            Id = string.IsNullOrWhiteSpace(requestedConversationId) ? Guid.NewGuid().ToString("N") : requestedConversationId,
            TenantId = context.TenantId,
            Title = BuildConversationTitle(openingMessage)
        };

        await _conversationRepository.UpsertAsync(conversation, cancellationToken);
        _logger.LogInformation(
            "Created conversation {ConversationId} for tenant {TenantId}.",
            conversation.Id,
            context.TenantId);
        return conversation;
    }

    private static string BuildConversationTitle(string openingMessage) =>
        openingMessage.Length <= 56 ? openingMessage : $"{openingMessage[..56]}...";

    private async Task<IReadOnlyCollection<FileAsset>> LoadAttachmentsAsync(
        IReadOnlyCollection<string>? attachmentIds,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (attachmentIds is null || attachmentIds.Count == 0)
        {
            return Array.Empty<FileAsset>();
        }

        var attachments = await _fileAssetRepository.ListByIdsAsync(attachmentIds, context.TenantId, cancellationToken);
        if (attachments.Count != attachmentIds.Count)
        {
            _logger.LogWarning(
                "Only {LoadedAttachmentCount} of {RequestedAttachmentCount} attachments were found for tenant {TenantId}.",
                attachments.Count,
                attachmentIds.Count,
                context.TenantId);
        }

        return attachments;
    }

    private async Task<IReadOnlyCollection<ConversationMessage>> AttachOperationToUserMessageAndLoadHistoryAsync(
        ConversationMessage userMessage,
        string operationId,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        userMessage.OperationId = operationId;
        await _conversationMessageRepository.UpsertAsync(userMessage, cancellationToken);
        await _conversationHistoryCompactionService.RefreshAsync(context.TenantId, userMessage.ConversationId, cancellationToken);

        return await _conversationMessageRepository.ListByConversationAsync(userMessage.ConversationId, context.TenantId, cancellationToken);
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

        await _conversationHistoryCompactionService.RefreshAsync(context.TenantId, conversationId, CancellationToken.None);
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
            task.InteractionMode.ToString(),
            task.InstructionText,
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
            asset.Kind.ToString(),
            asset.SizeBytes,
            asset.UploadedAtUtc);
}
