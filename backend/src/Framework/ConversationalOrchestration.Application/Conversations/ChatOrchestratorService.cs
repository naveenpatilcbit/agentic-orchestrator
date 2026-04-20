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
    private readonly IConversationTranscriptService _conversationTranscriptService;
    private readonly IAgentOperationRepository _operationRepository;
    private readonly IReviewTaskRepository _reviewTaskRepository;
    private readonly IAuditEventRepository _auditEventRepository;
    private readonly IFileAssetRepository _fileAssetRepository;
    private readonly IAgentCatalog _agentCatalog;
    private readonly IReviewTaskService _reviewTaskService;
    private readonly IConversationHistoryCompactionService _conversationHistoryCompactionService;
    private readonly IMasterOrchestrationAgent _masterOrchestrationAgent;
    private readonly ILogger<ChatOrchestratorService> _logger;

    public ChatOrchestratorService(
        IConversationRepository conversationRepository,
        IConversationMessageRepository conversationMessageRepository,
        IConversationTranscriptService conversationTranscriptService,
        IAgentOperationRepository operationRepository,
        IReviewTaskRepository reviewTaskRepository,
        IAuditEventRepository auditEventRepository,
        IFileAssetRepository fileAssetRepository,
        IAgentCatalog agentCatalog,
        IReviewTaskService reviewTaskService,
        IConversationHistoryCompactionService conversationHistoryCompactionService,
        IMasterOrchestrationAgent masterOrchestrationAgent,
        ILogger<ChatOrchestratorService> logger)
    {
        _conversationRepository = conversationRepository;
        _conversationMessageRepository = conversationMessageRepository;
        _conversationTranscriptService = conversationTranscriptService;
        _operationRepository = operationRepository;
        _reviewTaskRepository = reviewTaskRepository;
        _auditEventRepository = auditEventRepository;
        _fileAssetRepository = fileAssetRepository;
        _agentCatalog = agentCatalog;
        _reviewTaskService = reviewTaskService;
        _conversationHistoryCompactionService = conversationHistoryCompactionService;
        _masterOrchestrationAgent = masterOrchestrationAgent;
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

        var userMessage = await _conversationTranscriptService.AppendAsync(
            new ConversationTranscriptAppendRequest(
                context.TenantId,
                conversation.Id,
                context.UserId,
                ConversationMessageRole.User,
                request.Message,
                OperationId: null,
                SourceType: "chat-user",
                SourceMessageId: request.ClientMessageId,
                DeduplicationKey: $"chat-user:{request.ClientMessageId ?? Guid.NewGuid().ToString("N")}"),
            cancellationToken);

        // Each thread carries one active operation at a time. We load session state and let the
        // master agent decide whether to continue, start, or answer directly.
        var operations = await _operationRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);
        var reviewTasks = await _reviewTaskRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);
        var activeOperation = ConversationSessionStateResolver.ResolveActiveOperation(conversation, operations);
        var activeReviewTask = ConversationSessionStateResolver.ResolveActiveReviewTask(activeOperation, reviewTasks);

        // Keep "approve/reject" chat replies stable by short-circuiting into the existing review handler.
        if (activeReviewTask is not null &&
            (request.Message.Contains("approve", StringComparison.OrdinalIgnoreCase) ||
             request.Message.Contains("reject", StringComparison.OrdinalIgnoreCase) ||
             request.Message.Contains("needs changes", StringComparison.OrdinalIgnoreCase)))
        {
            await HandleReviewResponseAsync(
                userMessage,
                new RoutingDecision(
                    RoutingDecisionType.RespondToReviewTask,
                    activeOperation?.AgentId,
                    activeReviewTask.OperationId,
                    activeReviewTask.Id,
                    Explanation: "Chat text matched a review decision while a review task is open."),
                request.Message,
                context,
                cancellationToken);

            return (await GetSnapshotAsync(conversation.Id, context, cancellationToken))!;
        }

        var masterResult = await _masterOrchestrationAgent.RunAsync(
            new MasterOrchestrationRequest(
                request.Message,
                conversation,
                userMessage,
                operations,
                reviewTasks,
                attachments,
                context),
            cancellationToken);

        await AddAssistantMessageAsync(
            conversation.Id,
            context,
            masterResult.AssistantMessage,
            masterResult.OperationId ?? conversation.ActiveOperationId,
            "chat",
            masterResult.Actions,
            sourceMessageId: userMessage.Id,
            deduplicationKey: $"master-agent:{userMessage.Id}");

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

        var messages = await _conversationTranscriptService.ListByConversationAsync(conversationId, context.TenantId, cancellationToken);
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
            var messages = await _conversationTranscriptService.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);
            var operations = await _operationRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);
            var reviewTasks = await _reviewTaskRepository.ListByConversationAsync(conversation.Id, context.TenantId, cancellationToken);
            var activeOperation = ConversationSessionStateResolver.ResolveActiveOperation(conversation, operations);
            var activeReviewTask = ConversationSessionStateResolver.ResolveActiveReviewTask(activeOperation, reviewTasks);

            summaries.Add(new ConversationSummaryDto(
                conversation.Id,
                conversation.Title,
                messages.Count,
                activeOperation is null ? 0 : 1,
                activeReviewTask is null ? 0 : 1,
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
            Status = AgentOperationStatus.Received,
            SourceOperationId = routingDecision.SourceOperationId,
            SourceOutputId = routingDecision.SourceOutputId
        };
        // If the router selected a prior completed output as the source for this request,
        // persist it on the operation so the agent can load that lineage without re-parsing
        // the user text.
        if (!string.IsNullOrWhiteSpace(routingDecision.SourceOperationId) ||
            !string.IsNullOrWhiteSpace(routingDecision.SourceOutputId))
        {
            // Seed resolved lineage inputs onto the operation itself so agents can consume follow-up
            // work from completed outputs without re-parsing those ids from user chat text.
            operation.DataJson = JsonContent.Serialize(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                [StandardAgentInputNames.SourceOperationId] = routingDecision.SourceOperationId,
                [StandardAgentInputNames.SourceOutputId] = routingDecision.SourceOutputId
            });
        }

        await _operationRepository.UpsertAsync(operation, cancellationToken);
        _logger.LogInformation(
            "Starting new operation {OperationId} using agent {AgentId} for tenant {TenantId} conversation {ConversationId}.",
            operation.Id,
            agent.Definition.Id,
            context.TenantId,
            conversation.Id);
        conversation.ActiveOperationId = operation.Id;
        conversation.LastFocusedOperationId = operation.Id;
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _conversationRepository.UpsertAsync(conversation, cancellationToken);
        try
        {
            var conversationHistory = await AttachOperationToUserMessageAndLoadHistoryAsync(userMessage, operation.Id, context, cancellationToken);
            var result = await agent.StartAsync(conversation, conversationHistory, userMessage, operation, attachments, context, cancellationToken);
            await PersistAgentResultAsync(conversation, userMessage, result, context, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Agent {AgentId} failed to start operation {OperationId} for tenant {TenantId} conversation {ConversationId}.",
                agent.Definition.Id,
                operation.Id,
                context.TenantId,
                conversation.Id);

            operation.Status = AgentOperationStatus.Failed;
            operation.CurrentStep = "StartFailed";
            operation.PendingClarification = "The operation could not be started. Retry the request from this conversation once the issue is addressed.";
            operation.Summary = exception.Message;
            operation.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _operationRepository.UpsertAsync(operation, cancellationToken);

            await _auditEventRepository.AddAsync(
                new AuditEvent
                {
                    TenantId = context.TenantId,
                    ConversationId = conversation.Id,
                    OperationId = operation.Id,
                    EventType = "AgentStartFailed",
                    ActorType = "Framework",
                    ActorId = nameof(ChatOrchestratorService),
                    DataJson = JsonContent.Serialize(new
                    {
                        agentId = agent.Definition.Id,
                        error = exception.Message
                    })
                },
                cancellationToken);

            if (string.Equals(conversation.ActiveOperationId, operation.Id, StringComparison.Ordinal))
            {
                conversation.ActiveOperationId = null;
                conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await _conversationRepository.UpsertAsync(conversation, cancellationToken);
            }

            await AddAssistantMessageAsync(
                conversation.Id,
                context,
                $"I couldn't start {agent.Definition.DisplayName} yet: {exception.Message}",
                operation.Id,
                "status",
                sourceMessageId: userMessage.Id,
                deduplicationKey: $"agent-start-failed:{userMessage.Id}:{operation.Id}");
        }
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
            await AddAssistantMessageAsync(
                conversation.Id,
                context,
                "I couldn't find that operation anymore, so please restate the request and I'll start a fresh one.",
                null,
                "routing",
                sourceMessageId: userMessage.Id,
                deduplicationKey: $"missing-operation:{userMessage.Id}:{routingDecision.OperationId}");
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
        conversation.ActiveOperationId = operation.Id;
        conversation.LastFocusedOperationId = operation.Id;
        conversation.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _conversationRepository.UpsertAsync(conversation, cancellationToken);
        var conversationHistory = await AttachOperationToUserMessageAndLoadHistoryAsync(userMessage, operation.Id, context, cancellationToken);
        var result = await agent.ContinueAsync(conversation, conversationHistory, userMessage, operation, attachments, context, cancellationToken);
        await PersistAgentResultAsync(conversation, userMessage, result, context, cancellationToken);
    }

    private async Task HandleReviewResponseAsync(
        ConversationMessage userMessage,
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
            await AddAssistantMessageAsync(
                result.ConversationId,
                context,
                result.AssistantMessage,
                result.OperationId,
                "review",
                sourceMessageId: userMessage.Id,
                deduplicationKey: $"review-response:{userMessage.Id}");
        }
    }

    private async Task HandleStatusRequestAsync(
        ConversationThread conversation,
        ConversationMessage userMessage,
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
            var activeOperation = ConversationSessionStateResolver.ResolveActiveOperation(conversation, operations);
            if (activeOperation is null)
            {
                response = "There is no active workflow in this thread right now.";
            }
            else
            {
                operationId = activeOperation.Id;
                response = await _agentCatalog.Resolve(activeOperation.AgentId)
                    .DescribeStatusAsync(activeOperation, reviewTasks.Where(task => task.OperationId == activeOperation.Id).ToArray(), cancellationToken);
            }
        }

        await AddAssistantMessageAsync(
            conversation.Id,
            context,
            response,
            operationId,
            "status",
            sourceMessageId: userMessage.Id,
            deduplicationKey: $"status:{userMessage.Id}:{operationId ?? "thread"}");
    }

    private async Task PersistAgentResultAsync(
        ConversationThread conversation,
        ConversationMessage userMessage,
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

        var nextActiveOperation = ConversationSessionStateResolver.IsActiveOperation(result.Operation)
            ? result.Operation
            : null;
        ConversationSessionStateResolver.SyncConversationPointers(conversation, nextActiveOperation);
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
            result.Actions,
            sourceMessageId: userMessage.Id,
            deduplicationKey: $"agent-result:{userMessage.Id}:{result.Operation.Id}:chat");

        if (!string.IsNullOrWhiteSpace(result.FollowUpSystemMessage))
        {
            await AddAssistantMessageAsync(
                conversation.Id,
                context,
                result.FollowUpSystemMessage,
                result.Operation.Id,
                "status",
                sourceMessageId: userMessage.Id,
                deduplicationKey: $"agent-result:{userMessage.Id}:{result.Operation.Id}:status",
                role: ConversationMessageRole.System);
        }
        // Audit events are persisted separately from the transcript so we can keep an
        // immutable operational log for debugging and compliance without polluting chat.
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
                    existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
                    await _conversationRepository.UpsertAsync(existing, cancellationToken);
                }

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

        return await _conversationTranscriptService.ListByConversationAsync(userMessage.ConversationId, context.TenantId, cancellationToken);
    }

    private async Task AddAssistantMessageAsync(
        string conversationId,
        TenantExecutionContext context,
        string content,
        string? operationId,
        string messageKind,
        IReadOnlyCollection<AgentAction>? actions = null,
        string? sourceMessageId = null,
        string? deduplicationKey = null,
        ConversationMessageRole role = ConversationMessageRole.Assistant)
    {
        await _conversationTranscriptService.AppendAsync(
            new ConversationTranscriptAppendRequest(
                context.TenantId,
                conversationId,
                role == ConversationMessageRole.System ? "system" : "assistant",
                role,
                content,
                messageKind,
                OperationId: operationId,
                Actions: actions,
                SourceType: role == ConversationMessageRole.System ? "system-generated" : "assistant-generated",
                SourceMessageId: sourceMessageId,
                DeduplicationKey: deduplicationKey),
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
