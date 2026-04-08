using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Domain.Workflows;
using ConversationalOrchestration.Infrastructure.Data;
using MongoDB.Driver;

namespace ConversationalOrchestration.Infrastructure.Repositories;

public sealed class WorkflowInstanceRepository : IWorkflowInstanceRepository
{
    private readonly MongoCollections _collections;

    public WorkflowInstanceRepository(MongoCollections collections)
    {
        _collections = collections;
    }

    public async Task<WorkflowInstance?> GetAsync(string workflowInstanceId, string tenantId, CancellationToken cancellationToken) =>
        await _collections.WorkflowInstances.Find(item => item.Id == workflowInstanceId && item.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<WorkflowInstance?> GetByOperationAsync(string operationId, string tenantId, CancellationToken cancellationToken) =>
        await _collections.WorkflowInstances.Find(item => item.OperationId == operationId && item.TenantId == tenantId)
            .SortByDescending(item => item.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task UpsertAsync(WorkflowInstance instance, CancellationToken cancellationToken) =>
        _collections.WorkflowInstances.ReplaceOneAsync(
            item => item.Id == instance.Id && item.TenantId == instance.TenantId,
            instance,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
}

public sealed class WorkflowPendingRequestRepository : IWorkflowPendingRequestRepository
{
    private readonly MongoCollections _collections;

    public WorkflowPendingRequestRepository(MongoCollections collections)
    {
        _collections = collections;
    }

    public async Task<WorkflowPendingRequest?> GetAsync(string pendingRequestId, string tenantId, CancellationToken cancellationToken) =>
        await _collections.WorkflowPendingRequests.Find(item => item.Id == pendingRequestId && item.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<WorkflowPendingRequest?> GetByRequestIdAsync(string workflowInstanceId, string requestId, string tenantId, CancellationToken cancellationToken) =>
        await _collections.WorkflowPendingRequests.Find(item =>
                item.WorkflowInstanceId == workflowInstanceId &&
                item.RequestId == requestId &&
                item.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<WorkflowPendingRequest?> GetLatestOpenByOperationAsync(string operationId, string tenantId, CancellationToken cancellationToken) =>
        await _collections.WorkflowPendingRequests.Find(item =>
                item.OperationId == operationId &&
                item.TenantId == tenantId &&
                item.Status == WorkflowPendingRequestStatus.Open)
            .SortByDescending(item => item.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyCollection<WorkflowPendingRequest>> ListByWorkflowInstanceAsync(string workflowInstanceId, string tenantId, CancellationToken cancellationToken) =>
        await _collections.WorkflowPendingRequests.Find(item =>
                item.WorkflowInstanceId == workflowInstanceId &&
                item.TenantId == tenantId)
            .SortByDescending(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);

    public Task UpsertAsync(WorkflowPendingRequest request, CancellationToken cancellationToken) =>
        _collections.WorkflowPendingRequests.ReplaceOneAsync(
            item => item.Id == request.Id && item.TenantId == request.TenantId,
            request,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
}
