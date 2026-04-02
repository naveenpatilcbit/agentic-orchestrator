using FundOrchestrator.Application.Abstractions;
using FundOrchestrator.Domain.Auditing;
using FundOrchestrator.Domain.Conversations;
using FundOrchestrator.Domain.Files;
using FundOrchestrator.Domain.Operations;
using FundOrchestrator.Domain.Reviews;
using FundOrchestrator.Infrastructure.Data;
using MongoDB.Driver;

namespace FundOrchestrator.Infrastructure.Repositories;

public sealed class ConversationRepository : IConversationRepository
{
    private readonly MongoCollections _collections;

    public ConversationRepository(MongoCollections collections)
    {
        _collections = collections;
    }

    public async Task<ConversationThread?> GetAsync(string conversationId, string tenantId, CancellationToken cancellationToken) =>
        await _collections.Conversations.Find(item => item.Id == conversationId && item.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyCollection<ConversationThread>> ListByTenantAsync(string tenantId, CancellationToken cancellationToken) =>
        await _collections.Conversations.Find(item => item.TenantId == tenantId)
            .SortByDescending(item => item.UpdatedAtUtc)
            .ToListAsync(cancellationToken);

    public Task UpsertAsync(ConversationThread conversation, CancellationToken cancellationToken) =>
        _collections.Conversations.ReplaceOneAsync(
            item => item.Id == conversation.Id && item.TenantId == conversation.TenantId,
            conversation,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
}

public sealed class ConversationMessageRepository : IConversationMessageRepository
{
    private readonly MongoCollections _collections;

    public ConversationMessageRepository(MongoCollections collections)
    {
        _collections = collections;
    }

    public Task AddAsync(ConversationMessage message, CancellationToken cancellationToken) =>
        _collections.Messages.InsertOneAsync(message, cancellationToken: cancellationToken);

    public async Task<IReadOnlyCollection<ConversationMessage>> ListByConversationAsync(string conversationId, string tenantId, CancellationToken cancellationToken) =>
        await _collections.Messages.Find(item => item.ConversationId == conversationId && item.TenantId == tenantId)
            .SortBy(item => item.CreatedAtUtc)
            .ToListAsync(cancellationToken);
}

public sealed class AgentOperationRepository : IAgentOperationRepository
{
    private readonly MongoCollections _collections;

    public AgentOperationRepository(MongoCollections collections)
    {
        _collections = collections;
    }

    public async Task<AgentOperation?> GetAsync(string operationId, string tenantId, CancellationToken cancellationToken) =>
        await _collections.Operations.Find(item => item.Id == operationId && item.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyCollection<AgentOperation>> ListByConversationAsync(string conversationId, string tenantId, CancellationToken cancellationToken) =>
        await _collections.Operations.Find(item => item.ConversationId == conversationId && item.TenantId == tenantId)
            .SortByDescending(item => item.UpdatedAtUtc)
            .ToListAsync(cancellationToken);

    public Task UpsertAsync(AgentOperation operation, CancellationToken cancellationToken) =>
        _collections.Operations.ReplaceOneAsync(
            item => item.Id == operation.Id && item.TenantId == operation.TenantId,
            operation,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
}

public sealed class ReviewTaskRepository : IReviewTaskRepository
{
    private readonly MongoCollections _collections;

    public ReviewTaskRepository(MongoCollections collections)
    {
        _collections = collections;
    }

    public async Task<ReviewTask?> GetAsync(string reviewTaskId, string tenantId, CancellationToken cancellationToken) =>
        await _collections.ReviewTasks.Find(item => item.Id == reviewTaskId && item.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyCollection<ReviewTask>> ListByConversationAsync(string conversationId, string tenantId, CancellationToken cancellationToken) =>
        await _collections.ReviewTasks.Find(item => item.ConversationId == conversationId && item.TenantId == tenantId)
            .SortByDescending(item => item.UpdatedAtUtc)
            .ToListAsync(cancellationToken);

    public Task UpsertAsync(ReviewTask task, CancellationToken cancellationToken) =>
        _collections.ReviewTasks.ReplaceOneAsync(
            item => item.Id == task.Id && item.TenantId == task.TenantId,
            task,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
}

public sealed class AuditEventRepository : IAuditEventRepository
{
    private readonly MongoCollections _collections;

    public AuditEventRepository(MongoCollections collections)
    {
        _collections = collections;
    }

    public Task AddAsync(AuditEvent auditEvent, CancellationToken cancellationToken) =>
        _collections.AuditEvents.InsertOneAsync(auditEvent, cancellationToken: cancellationToken);
}

public sealed class FileAssetRepository : IFileAssetRepository
{
    private readonly MongoCollections _collections;

    public FileAssetRepository(MongoCollections collections)
    {
        _collections = collections;
    }

    public async Task<FileAsset?> GetAsync(string fileAssetId, string tenantId, CancellationToken cancellationToken) =>
        await _collections.Files.Find(item => item.Id == fileAssetId && item.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyCollection<FileAsset>> ListByConversationAsync(string conversationId, string tenantId, CancellationToken cancellationToken) =>
        await _collections.Files.Find(item => item.ConversationId == conversationId && item.TenantId == tenantId)
            .SortByDescending(item => item.UploadedAtUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyCollection<FileAsset>> ListByIdsAsync(IReadOnlyCollection<string> ids, string tenantId, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return Array.Empty<FileAsset>();
        }

        var filter = Builders<FileAsset>.Filter.In(item => item.Id, ids)
                     & Builders<FileAsset>.Filter.Eq(item => item.TenantId, tenantId);
        return await _collections.Files.Find(filter).ToListAsync(cancellationToken);
    }

    public Task AddAsync(FileAsset fileAsset, CancellationToken cancellationToken) =>
        _collections.Files.InsertOneAsync(fileAsset, cancellationToken: cancellationToken);
}
