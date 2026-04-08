using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Domain.Workflows;
using Microsoft.Agents.AI.Workflows;
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

    public WorkflowRuntimeService(
        IWorkflowRegistry workflowRegistry,
        IWorkflowInstanceRepository workflowInstanceRepository,
        IWorkflowPendingRequestRepository workflowPendingRequestRepository,
        MongoJsonCheckpointStore checkpointStore)
    {
        _workflowRegistry = workflowRegistry;
        _workflowInstanceRepository = workflowInstanceRepository;
        _workflowPendingRequestRepository = workflowPendingRequestRepository;
        _serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _checkpointManager = CheckpointManager.CreateJson(checkpointStore, _serializerOptions);
    }

    public async Task<WorkflowRunResult> StartAsync(
        WorkflowStartRequest request,
        CancellationToken cancellationToken)
    {
        var definition = _workflowRegistry.Resolve(request.WorkflowName);
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

        await using var run = await RunWorkflowAsync(definition, request.Input, instance.Id, cancellationToken);
        return await PersistRunAsync(definition, instance, run, cancellationToken);
    }

    public async Task<WorkflowRunResult> ResumeAsync(
        WorkflowResumeRequest request,
        CancellationToken cancellationToken)
    {
        var instance = await _workflowInstanceRepository.GetAsync(request.WorkflowInstanceId, request.TenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow instance '{request.WorkflowInstanceId}' was not found.");
        var pendingRequest = await _workflowPendingRequestRepository.GetAsync(request.PendingRequestId, request.TenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Workflow pending request '{request.PendingRequestId}' was not found.");

        var definition = _workflowRegistry.Resolve(instance.WorkflowName);
        var checkpointId = request.CheckpointId ?? instance.LatestCheckpointId
            ?? throw new InvalidOperationException($"Workflow instance '{instance.Id}' does not have a checkpoint to resume.");

        pendingRequest.Status = WorkflowPendingRequestStatus.Responded;
        pendingRequest.ResponsePayloadJson = request.ResponsePayloadJson;
        pendingRequest.RespondedAtUtc = DateTimeOffset.UtcNow;
        pendingRequest.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _workflowPendingRequestRepository.UpsertAsync(pendingRequest, cancellationToken);

        instance.Status = WorkflowInstanceStatus.Running;
        instance.CurrentStep = $"Resuming:{pendingRequest.PortId}";
        instance.LastPendingRequestId = pendingRequest.Id;
        instance.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await _workflowInstanceRepository.UpsertAsync(instance, cancellationToken);

        var checkpoint = new CheckpointInfo(instance.Id, checkpointId);
        await using var run = await InProcessExecution.ResumeAsync(definition.Build(), checkpoint, _checkpointManager, cancellationToken);

        _ = run.NewEvents.ToArray();
        var externalResponse = CreateExternalResponse(definition, pendingRequest);
        await run.ResumeAsync([externalResponse], cancellationToken);

        return await PersistRunAsync(definition, instance, run, cancellationToken);
    }

    public async Task<WorkflowRunResult> RetryAsync(
        WorkflowRetryRequest request,
        CancellationToken cancellationToken)
    {
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

        var checkpoint = new CheckpointInfo(instance.Id, checkpointId);
        await using var run = await InProcessExecution.ResumeAsync(definition.Build(), checkpoint, _checkpointManager, cancellationToken);
        return await PersistRunAsync(definition, instance, run, cancellationToken);
    }

    private async Task<Run> RunWorkflowAsync(
        IWorkflowDefinition definition,
        object input,
        string sessionId,
        CancellationToken cancellationToken)
    {
        dynamic payload = input;
        return await InProcessExecution.RunAsync(definition.Build(), payload, _checkpointManager, sessionId, cancellationToken);
    }

    private async Task<WorkflowRunResult> PersistRunAsync(
        IWorkflowDefinition definition,
        WorkflowInstance instance,
        Run run,
        CancellationToken cancellationToken)
    {
        var events = run.OutgoingEvents.ToArray();
        var pendingRequests = new List<WorkflowPendingRequest>();
        var outputs = new List<WorkflowOutputMessage>();
        var activatedExecutors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? latestCheckpointId = instance.LatestCheckpointId;
        string? errorMessage = null;

        foreach (var workflowEvent in events)
        {
            switch (workflowEvent)
            {
                case RequestInfoEvent requestInfoEvent:
                {
                    var pendingRequest = await UpsertPendingRequestAsync(instance, requestInfoEvent, cancellationToken);
                    pendingRequests.Add(pendingRequest);
                    break;
                }
                case WorkflowOutputEvent outputEvent:
                {
                    var data = outputEvent.Data;
                    if (data is not null)
                    {
                        outputs.Add(new WorkflowOutputMessage(
                            data.GetType().Name,
                            SerializeValue(data, data.GetType()),
                            outputEvent.ExecutorId));
                    }

                    break;
                }
                case WorkflowErrorEvent workflowErrorEvent:
                    errorMessage = workflowErrorEvent.Exception?.Message ?? workflowEvent.ToString();
                    break;
                case SuperStepCompletedEvent superStepCompletedEvent:
                    var completionInfo = superStepCompletedEvent.CompletionInfo;
                    if (completionInfo?.Checkpoint is not null)
                    {
                        latestCheckpointId = completionInfo.Checkpoint.CheckpointId;
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

        var status = await run.GetStatusAsync(cancellationToken);
        instance.LatestCheckpointId = latestCheckpointId;
        instance.LastPendingRequestId = pendingRequests.LastOrDefault()?.Id;
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

        return new WorkflowRunResult(
            instance,
            pendingRequests,
            outputs,
            activatedExecutors.ToArray(),
            errorMessage);
    }

    private async Task<WorkflowPendingRequest> UpsertPendingRequestAsync(
        WorkflowInstance instance,
        RequestInfoEvent requestInfoEvent,
        CancellationToken cancellationToken)
    {
        var requestData = requestInfoEvent.Request.Data;
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
        pendingRequest.RequestType = requestData?.GetType().Name ?? requestInfoEvent.Request.PortInfo.RequestType.ToString();
        pendingRequest.RequestPayloadJson = requestData is null
            ? "{}"
            : SerializeValue(requestData, requestData.GetType());
        pendingRequest.Status = WorkflowPendingRequestStatus.Open;
        pendingRequest.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _workflowPendingRequestRepository.UpsertAsync(pendingRequest, cancellationToken);
        return pendingRequest;
    }

    private ExternalResponse CreateExternalResponse(
        IWorkflowDefinition definition,
        WorkflowPendingRequest pendingRequest)
    {
        var portDescriptor = definition.ResolveRequestPort(pendingRequest.PortId);
        var requestPayload = DeserializeValue(pendingRequest.RequestPayloadJson, portDescriptor.RequestType);
        var responsePayload = DeserializeValue(
            pendingRequest.ResponsePayloadJson ?? pendingRequest.RequestPayloadJson,
            portDescriptor.ResponseType);

        var externalRequest = ExternalRequest.Create(portDescriptor.Port, requestPayload!, pendingRequest.RequestId);
        return externalRequest.CreateResponse(responsePayload!);
    }

    private object? DeserializeValue(string json, Type targetType) =>
        JsonSerializer.Deserialize(json, targetType, _serializerOptions);

    private string SerializeValue(object value, Type targetType) =>
        JsonSerializer.Serialize(value, targetType, _serializerOptions);
}
