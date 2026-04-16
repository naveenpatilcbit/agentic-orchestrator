using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Domain.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Declarative.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ConversationalOrchestration.Infrastructure.Workflows;

public sealed class WorkflowRegistry : IWorkflowRegistry
{
    private readonly IReadOnlyDictionary<string, IWorkflowDefinition> _definitions;

    public WorkflowRegistry(IEnumerable<IWorkflowDefinition> definitions)
    {
        _definitions = definitions.ToDictionary(definition => definition.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IWorkflowDefinition Resolve(string workflowName) =>
        _definitions.TryGetValue(workflowName, out var definition)
            ? definition
            : throw new InvalidOperationException($"Workflow '{workflowName}' is not registered.");
}

public sealed class WorkflowRuntimeService : IWorkflowRuntimeService
{
    private readonly IWorkflowRegistry _workflowRegistry;
    private readonly IWorkflowInstanceRepository _workflowInstanceRepository;
    private readonly IWorkflowPendingRequestRepository _workflowPendingRequestRepository;
    private readonly CheckpointManager _checkpointManager;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly ILogger<WorkflowRuntimeService> _logger;

    public WorkflowRuntimeService(
        IWorkflowRegistry workflowRegistry,
        IWorkflowInstanceRepository workflowInstanceRepository,
        IWorkflowPendingRequestRepository workflowPendingRequestRepository,
        MongoJsonCheckpointStore checkpointStore,
        ILogger<WorkflowRuntimeService> logger)
    {
        _workflowRegistry = workflowRegistry;
        _workflowInstanceRepository = workflowInstanceRepository;
        _workflowPendingRequestRepository = workflowPendingRequestRepository;
        _serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _checkpointManager = CheckpointManager.CreateJson(checkpointStore, _serializerOptions);
        _logger = logger;
    }

    public async Task<WorkflowRunResult> StartAsync<TInput>(
        WorkflowStartRequest<TInput> request,
        CancellationToken cancellationToken)
        where TInput : notnull
    {
        _logger.LogInformation(
            "Starting workflow {WorkflowName} for tenant {TenantId} conversation {ConversationId} operation {OperationId}. RequestedInstanceId={RequestedWorkflowInstanceId}",
            request.WorkflowName,
            request.TenantId,
            request.ConversationId,
            request.OperationId,
            request.WorkflowInstanceId);
        var definition = _workflowRegistry.Resolve(request.WorkflowName);
        ValidateStartInputType(definition, typeof(TInput));
        var instance = new WorkflowInstance
        {
            Id = request.WorkflowInstanceId ?? Guid.NewGuid().ToString("N"),
            TenantId = request.TenantId,
            OperationId = request.OperationId,
            ConversationId = request.ConversationId,
            WorkflowName = request.WorkflowName,
            Status = WorkflowInstanceStatus.Running,
            CurrentStep = "Starting"
        };

        await _workflowInstanceRepository.UpsertAsync(instance, cancellationToken);
        _logger.LogInformation(
            "Created workflow instance {WorkflowInstanceId} for workflow {WorkflowName} on operation {OperationId}.",
            instance.Id,
            request.WorkflowName,
            request.OperationId);

        var buildContext = CreateBuildContext(instance);
        await using var run = await RunWorkflowAsync(definition, request.Input, buildContext, cancellationToken);
        var processingResult = await ProcessRunAsync(definition, instance, run, cancellationToken);
        return await PersistRunAsync(instance, run, processingResult, cancellationToken);
    }

    public async Task<WorkflowRunResult> ResumeAsync(
        WorkflowResumeRequest request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Resuming workflow instance {WorkflowInstanceId} for tenant {TenantId}. PendingRequestId={PendingRequestId} CheckpointId={CheckpointId}",
            request.WorkflowInstanceId,
            request.TenantId,
            request.PendingRequestId,
            request.CheckpointId);
        var instance = await _workflowInstanceRepository.GetAsync(request.WorkflowInstanceId, request.TenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow instance '{request.WorkflowInstanceId}' was not found.");
        var pendingRequest = await _workflowPendingRequestRepository.GetAsync(request.PendingRequestId, request.TenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow pending request '{request.PendingRequestId}' was not found.");

        if (!string.Equals(pendingRequest.WorkflowInstanceId, instance.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Workflow pending request '{pendingRequest.Id}' does not belong to workflow instance '{instance.Id}'.");
        }

        if (pendingRequest.Status != WorkflowPendingRequestStatus.Open)
        {
            throw new InvalidOperationException(
                $"Workflow pending request '{pendingRequest.Id}' is already in status '{pendingRequest.Status}'.");
        }

        var definition = _workflowRegistry.Resolve(instance.WorkflowName);
        var checkpointId = request.CheckpointId ?? instance.LatestCheckpointId
            ?? throw new InvalidOperationException($"Workflow instance '{instance.Id}' does not have a checkpoint to resume.");
        pendingRequest.ResponsePayloadJson = request.ResponsePayloadJson;

        var buildContext = CreateBuildContext(instance);
        var checkpoint = new CheckpointInfo(instance.Id, checkpointId);
        await using var run = await InProcessExecution.ResumeAsync(definition.Build(buildContext), checkpoint, _checkpointManager, cancellationToken);

        _ = run.NewEvents.ToArray();
        var externalResponse = CreateExternalResponse(definition, pendingRequest);
        await run.ResumeAsync([externalResponse], cancellationToken);

        var resumedAtUtc = DateTimeOffset.UtcNow;
        pendingRequest.Status = WorkflowPendingRequestStatus.Responded;
        pendingRequest.RespondedAtUtc = resumedAtUtc;
        pendingRequest.UpdatedAtUtc = resumedAtUtc;
        await _workflowPendingRequestRepository.UpsertAsync(pendingRequest, cancellationToken);

        instance.Status = WorkflowInstanceStatus.Running;
        instance.CurrentStep = $"Resuming:{pendingRequest.PortId}";
        instance.LastPendingRequestId = pendingRequest.Id;
        instance.UpdatedAtUtc = resumedAtUtc;
        await _workflowInstanceRepository.UpsertAsync(instance, cancellationToken);

        var processingResult = await ProcessRunAsync(definition, instance, run, cancellationToken);
        return await PersistRunAsync(instance, run, processingResult, cancellationToken);
    }

    public async Task<WorkflowRunResult> RetryAsync(
        WorkflowRetryRequest request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Retrying workflow instance {WorkflowInstanceId} for tenant {TenantId}. CheckpointId={CheckpointId}",
            request.WorkflowInstanceId,
            request.TenantId,
            request.CheckpointId);
        var instance = await _workflowInstanceRepository.GetAsync(request.WorkflowInstanceId, request.TenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow instance '{request.WorkflowInstanceId}' was not found.");
        var definition = _workflowRegistry.Resolve(instance.WorkflowName);
        var checkpointId = request.CheckpointId ?? instance.LatestCheckpointId
            ?? throw new InvalidOperationException($"Workflow instance '{instance.Id}' does not have a checkpoint to retry.");

        instance.RetryCount += 1;
        instance.Status = WorkflowInstanceStatus.Running;
        instance.CurrentStep = "Retrying";
        instance.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _workflowInstanceRepository.UpsertAsync(instance, cancellationToken);

        var buildContext = CreateBuildContext(instance);
        var checkpoint = new CheckpointInfo(instance.Id, checkpointId);
        await using var run = await InProcessExecution.ResumeAsync(definition.Build(buildContext), checkpoint, _checkpointManager, cancellationToken);
        var processingResult = await ProcessRunAsync(definition, instance, run, cancellationToken);
        return await PersistRunAsync(instance, run, processingResult, cancellationToken);
    }

    private async Task<Run> RunWorkflowAsync<TInput>(
        IWorkflowDefinition definition,
        TInput input,
        WorkflowBuildContext buildContext,
        CancellationToken cancellationToken)
        where TInput : notnull
    {
        return await InProcessExecution.RunAsync(definition.Build(buildContext), input!, _checkpointManager, buildContext.WorkflowInstanceId, cancellationToken);
    }

    private async Task<RunProcessingResult> ProcessRunAsync(
        IWorkflowDefinition definition,
        WorkflowInstance instance,
        Run run,
        CancellationToken cancellationToken)
    {
        var pendingRequests = new List<WorkflowPendingRequest>();
        var outputs = new List<WorkflowOutputMessage>();
        var activatedExecutors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? latestCheckpointId = instance.LatestCheckpointId;
        string? errorMessage = null;

        while (true)
        {
            var events = run.NewEvents.ToArray();
            if (events.Length == 0)
            {
                break;
            }

            var automaticResponses = new List<ExternalResponse>();
            foreach (var workflowEvent in events)
            {
                switch (workflowEvent)
                {
                    case RequestInfoEvent requestInfoEvent:
                    {
                        // Request ports can either pause for an external reply or be auto-satisfied by
                        // the workflow definition. Both paths still need to preserve the same request id.
                        var automaticResponse = await definition.TryCreateAutomaticResponseAsync(
                            requestInfoEvent,
                            cancellationToken);
                        if (automaticResponse is not null)
                        {
                            _logger.LogInformation(
                                "Workflow instance {WorkflowInstanceId} produced automatic response for port {PortId}.",
                                instance.Id,
                                requestInfoEvent.Request.PortInfo.PortId);
                            automaticResponses.Add(automaticResponse);
                            break;
                        }

                        var pendingRequest = await UpsertPendingRequestAsync(
                            definition,
                            instance,
                            requestInfoEvent,
                            cancellationToken);
                        _logger.LogInformation(
                            "Workflow instance {WorkflowInstanceId} is waiting for external input. PendingRequestId={PendingRequestId} PortId={PortId} RequestId={RequestId}",
                            instance.Id,
                            pendingRequest.Id,
                            pendingRequest.PortId,
                            pendingRequest.RequestId);
                        pendingRequests.Add(pendingRequest);
                        break;
                    }
                    case WorkflowOutputEvent outputEvent:
                    {
                        var data = outputEvent.Data;
                        if (data is not null)
                        {
                            if (data is AgentResponse agentResponse && !string.IsNullOrWhiteSpace(agentResponse.Text))
                            {
                                outputs.Add(new WorkflowOutputMessage(
                                    "MessageActivity",
                                    new WorkflowActivityPayload(
                                        agentResponse.Text,
                                        ChatRole.Assistant.Value),
                                    SerializeValue(
                                        new WorkflowActivityPayload(
                                            agentResponse.Text,
                                            ChatRole.Assistant.Value),
                                        typeof(WorkflowActivityPayload)),
                                    outputEvent.ExecutorId));
                                break;
                            }

                            outputs.Add(new WorkflowOutputMessage(
                                data.GetType().Name,
                                data,
                                SerializeValue(data, data.GetType()),
                                outputEvent.ExecutorId));
                        }

                        break;
                    }
                    case WorkflowErrorEvent workflowErrorEvent:
                        _logger.LogError(
                            workflowErrorEvent.Exception,
                            "Workflow instance {WorkflowInstanceId} emitted an error event.",
                            instance.Id);
                        errorMessage = workflowErrorEvent.Exception?.Message ?? workflowEvent.ToString();
                        break;
                    case SuperStepCompletedEvent superStepCompletedEvent:
                    {
                        var completionInfo = superStepCompletedEvent.CompletionInfo;
                        if (completionInfo?.Checkpoint is not null)
                        {
                            latestCheckpointId = completionInfo.Checkpoint.CheckpointId;
                            _logger.LogInformation(
                                "Workflow instance {WorkflowInstanceId} completed super step. CheckpointId={CheckpointId}",
                                instance.Id,
                                latestCheckpointId);
                        }

                        if (completionInfo is null)
                        {
                            break;
                        }

                        foreach (var executorId in completionInfo.ActivatedExecutors)
                        {
                            activatedExecutors.Add(executorId);
                        }

                        break;
                    }
                }
            }

            if (automaticResponses.Count == 0)
            {
                break;
            }

            await run.ResumeAsync(automaticResponses, cancellationToken);
        }

        return new RunProcessingResult(
            pendingRequests,
            outputs,
            activatedExecutors.ToArray(),
            latestCheckpointId,
            errorMessage);
    }

    private async Task<WorkflowRunResult> PersistRunAsync(
        WorkflowInstance instance,
        Run run,
        RunProcessingResult processingResult,
        CancellationToken cancellationToken)
    {
        var pendingRequests = processingResult.PendingRequests;
        var outputs = processingResult.Outputs;
        var activatedExecutors = processingResult.ActivatedExecutors;
        var latestCheckpointId = processingResult.LatestCheckpointId;
        var errorMessage = processingResult.ErrorMessage;

        var status = await run.GetStatusAsync(cancellationToken);
        instance.LatestCheckpointId = latestCheckpointId;
        instance.LastPendingRequestId = pendingRequests.LastOrDefault()?.Id ?? instance.LastPendingRequestId;
        instance.UpdatedAtUtc = DateTimeOffset.UtcNow;
        instance.LastError = errorMessage;
        instance.Status = status switch
        {
            RunStatus.PendingRequests => WorkflowInstanceStatus.WaitingForHumanInput,
            RunStatus.Ended when string.IsNullOrWhiteSpace(errorMessage) => WorkflowInstanceStatus.Completed,
            RunStatus.Ended => WorkflowInstanceStatus.Failed,
            _ when !string.IsNullOrWhiteSpace(errorMessage) => WorkflowInstanceStatus.Failed,
            _ => WorkflowInstanceStatus.Running
        };
        instance.CurrentStep = pendingRequests.LastOrDefault()?.PortId
            ?? activatedExecutors.LastOrDefault()
            ?? (instance.Status == WorkflowInstanceStatus.Completed ? "Completed" : instance.CurrentStep);

        await _workflowInstanceRepository.UpsertAsync(instance, cancellationToken);
        _logger.LogInformation(
            "Persisted workflow instance {WorkflowInstanceId}. Status={Status} CurrentStep={CurrentStep} CheckpointId={CheckpointId} PendingRequests={PendingRequestCount} Outputs={OutputCount} Error={ErrorMessage}",
            instance.Id,
            instance.Status,
            instance.CurrentStep,
            instance.LatestCheckpointId,
            pendingRequests.Count,
            outputs.Count,
            errorMessage);

        return new WorkflowRunResult(
            instance,
            pendingRequests,
            outputs,
            activatedExecutors,
            errorMessage);
    }

    private async Task<WorkflowPendingRequest> UpsertPendingRequestAsync(
        IWorkflowDefinition definition,
        WorkflowInstance instance,
        RequestInfoEvent requestInfoEvent,
        CancellationToken cancellationToken)
    {
        var portDescriptor = definition.ResolveRequestPort(requestInfoEvent.Request.PortInfo.PortId);
        var requestData = requestInfoEvent.Request.TryGetDataAs(portDescriptor.RequestType, out var typedRequest)
            ? typedRequest
            : requestInfoEvent.Request.Data;
        var existing = await _workflowPendingRequestRepository.GetByRequestIdAsync(instance.Id, requestInfoEvent.Request.RequestId, instance.TenantId, cancellationToken);

        var pendingRequest = existing ?? new WorkflowPendingRequest
        {
            TenantId = instance.TenantId,
            WorkflowInstanceId = instance.Id,
            OperationId = instance.OperationId,
            ConversationId = instance.ConversationId
        };

        pendingRequest.PortId = requestInfoEvent.Request.PortInfo.PortId;
        pendingRequest.RequestId = requestInfoEvent.Request.RequestId;
        pendingRequest.RequestType = requestInfoEvent.Request.PortInfo.RequestType.ToString();
        // Persist the typed request payload as JSON so review tasks and later resume calls can
        // reconstruct the exact workflow request outside the in-memory run.
        pendingRequest.RequestPayloadJson = requestData is null
            ? "{}"
            : SerializeValue(requestData, requestData.GetType());
        pendingRequest.PromptText = TryExtractPromptText(requestInfoEvent);
        pendingRequest.Status = WorkflowPendingRequestStatus.Open;
        pendingRequest.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _workflowPendingRequestRepository.UpsertAsync(pendingRequest, cancellationToken);
        _logger.LogDebug(
            "Upserted workflow pending request {PendingRequestId} for workflow instance {WorkflowInstanceId}. PortId={PortId} RequestId={RequestId}",
            pendingRequest.Id,
            instance.Id,
            pendingRequest.PortId,
            pendingRequest.RequestId);
        return pendingRequest;
    }

    private ExternalResponse CreateExternalResponse(
        IWorkflowDefinition definition,
        WorkflowPendingRequest pendingRequest)
    {
        var portDescriptor = definition.ResolveRequestPort(pendingRequest.PortId);
        var requestPayload = DeserializeValue(pendingRequest.RequestPayloadJson, portDescriptor.RequestType);
        // Resume has to recreate the original request envelope before attaching the response;
        // this is how the workflow runtime binds the human reply back to the correct request port.
        if (portDescriptor.ResponseType == typeof(ExternalInputResponse))
        {
            var resumePayload = JsonSerializer.Deserialize<ExternalInputResumePayload>(
                pendingRequest.ResponsePayloadJson ?? "{}",
                _serializerOptions);
            var resumableRequest = ExternalRequest.Create(portDescriptor.Port, requestPayload!, pendingRequest.RequestId);
            var response = new ExternalInputResponse(new ChatMessage(
                ParseChatRole(resumePayload?.Role),
                resumePayload?.MessageText ?? string.Empty));
            return resumableRequest.CreateResponse(response);
        }

        var responsePayload = DeserializeValue(
            pendingRequest.ResponsePayloadJson ?? pendingRequest.RequestPayloadJson,
            portDescriptor.ResponseType);

        var externalRequest = ExternalRequest.Create(portDescriptor.Port, requestPayload!, pendingRequest.RequestId);
        return externalRequest.CreateResponse(responsePayload!);
    }

    private static WorkflowBuildContext CreateBuildContext(WorkflowInstance instance) =>
        new(
            instance.TenantId,
            instance.ConversationId,
            instance.Id,
            instance.OperationId);

    private static string? TryExtractPromptText(RequestInfoEvent requestInfoEvent)
    {
        if (!requestInfoEvent.Request.TryGetDataAs(typeof(ExternalInputRequest), out var requestValue) ||
            requestValue is not ExternalInputRequest externalInputRequest)
        {
            return null;
        }

        var agentResponse = externalInputRequest.AgentResponse;
        if (agentResponse is null)
        {
            return null;
        }

        var directText = agentResponse.Text;
        if (!string.IsNullOrWhiteSpace(directText))
        {
            return directText;
        }

        var promptText = string.Join(
            "\n",
            agentResponse.Messages
                .Select(message => message.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));

        return string.IsNullOrWhiteSpace(promptText)
            ? null
            : promptText;
    }

    private static ChatRole ParseChatRole(string? role) =>
        role?.Trim().ToLowerInvariant() switch
        {
            "assistant" => ChatRole.Assistant,
            "system" => ChatRole.System,
            "tool" => ChatRole.Tool,
            _ => ChatRole.User
        };

    private object? DeserializeValue(string json, Type targetType) =>
        JsonSerializer.Deserialize(json, targetType, _serializerOptions);

    private string SerializeValue(object value, Type targetType) =>
        JsonSerializer.Serialize(value, targetType, _serializerOptions);

    private static void ValidateStartInputType(IWorkflowDefinition definition, Type inputType)
    {
        if (!definition.StartInputType.IsAssignableFrom(inputType))
        {
            throw new InvalidOperationException(
                $"Workflow '{definition.Name}' expects a start payload of type '{definition.StartInputType.Name}', but received '{inputType.Name}'.");
        }
    }

    private sealed record RunProcessingResult(
        IReadOnlyCollection<WorkflowPendingRequest> PendingRequests,
        IReadOnlyCollection<WorkflowOutputMessage> Outputs,
        IReadOnlyCollection<string> ActivatedExecutors,
        string? LatestCheckpointId,
        string? ErrorMessage);

    private sealed record WorkflowActivityPayload(
        string Text,
        string Role);
}
