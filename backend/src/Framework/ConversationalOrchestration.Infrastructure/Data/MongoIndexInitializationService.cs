using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using ConversationalOrchestration.Domain.Workflows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace ConversationalOrchestration.Infrastructure.Data;

public sealed class MongoIndexInitializationService : IHostedService
{
    private readonly MongoCollections _collections;
    private readonly ILogger<MongoIndexInitializationService> _logger;

    public MongoIndexInitializationService(
        MongoCollections collections,
        ILogger<MongoIndexInitializationService> logger)
    {
        _collections = collections;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureConversationIndexesAsync(cancellationToken);
        await EnsureMessageIndexesAsync(cancellationToken);
        await EnsureOperationIndexesAsync(cancellationToken);
        await EnsureOperationOutputIndexesAsync(cancellationToken);
        await EnsureReviewTaskIndexesAsync(cancellationToken);
        await EnsureFileIndexesAsync(cancellationToken);
        await EnsureWorkflowIndexesAsync(cancellationToken);
        await EnsureCheckpointIndexesAsync(cancellationToken);

        _logger.LogInformation("Mongo indexes for conversational orchestration infrastructure are ensured.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private Task EnsureConversationIndexesAsync(CancellationToken cancellationToken) =>
        _collections.Conversations.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<ConversationThread>(
                    Builders<ConversationThread>.IndexKeys
                        .Ascending(item => item.TenantId)
                        .Descending(item => item.UpdatedAtUtc)),
            ],
            cancellationToken: cancellationToken);

    private Task EnsureMessageIndexesAsync(CancellationToken cancellationToken) =>
        _collections.Messages.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<ConversationMessage>(
                    Builders<ConversationMessage>.IndexKeys
                        .Ascending(item => item.TenantId)
                        .Ascending(item => item.ConversationId)
                        .Ascending(item => item.DeduplicationKey)),
                new CreateIndexModel<ConversationMessage>(
                    Builders<ConversationMessage>.IndexKeys
                        .Ascending(item => item.TenantId)
                        .Ascending(item => item.ConversationId)
                        .Ascending(item => item.OperationId)
                        .Ascending(item => item.CreatedAtUtc)),
                new CreateIndexModel<ConversationMessage>(
                    Builders<ConversationMessage>.IndexKeys
                        .Ascending(item => item.TenantId)
                        .Ascending(item => item.ConversationId)
                        .Ascending(item => item.CreatedAtUtc))
            ],
            cancellationToken: cancellationToken);

    private Task EnsureOperationIndexesAsync(CancellationToken cancellationToken) =>
        _collections.Operations.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<AgentOperation>(
                    Builders<AgentOperation>.IndexKeys
                        .Ascending(item => item.TenantId)
                        .Ascending(item => item.ConversationId)
                        .Descending(item => item.UpdatedAtUtc))
            ],
            cancellationToken: cancellationToken);

    private Task EnsureOperationOutputIndexesAsync(CancellationToken cancellationToken) =>
        _collections.OperationOutputs.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<OperationOutput>(
                    Builders<OperationOutput>.IndexKeys
                        .Ascending(item => item.TenantId)
                        .Ascending(item => item.ConversationId)
                        .Descending(item => item.UpdatedAtUtc)),
                new CreateIndexModel<OperationOutput>(
                    Builders<OperationOutput>.IndexKeys
                        .Ascending(item => item.TenantId)
                        .Ascending(item => item.OperationId)
                        .Descending(item => item.UpdatedAtUtc)),
                new CreateIndexModel<OperationOutput>(
                    Builders<OperationOutput>.IndexKeys
                        .Ascending(item => item.TenantId)
                        .Ascending(item => item.OutputType)
                        .Descending(item => item.UpdatedAtUtc))
            ],
            cancellationToken: cancellationToken);

    private Task EnsureReviewTaskIndexesAsync(CancellationToken cancellationToken) =>
        _collections.ReviewTasks.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<ReviewTask>(
                    Builders<ReviewTask>.IndexKeys
                        .Ascending(item => item.TenantId)
                        .Ascending(item => item.ConversationId)
                        .Descending(item => item.UpdatedAtUtc)),
                new CreateIndexModel<ReviewTask>(
                    Builders<ReviewTask>.IndexKeys
                        .Ascending(item => item.TenantId)
                        .Ascending(item => item.OperationId)
                        .Ascending(item => item.Status)
                        .Descending(item => item.UpdatedAtUtc))
            ],
            cancellationToken: cancellationToken);

    private Task EnsureFileIndexesAsync(CancellationToken cancellationToken) =>
        _collections.Files.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<FileAsset>(
                    Builders<FileAsset>.IndexKeys
                        .Ascending(item => item.TenantId)
                        .Ascending(item => item.ConversationId)
                        .Descending(item => item.UploadedAtUtc))
            ],
            cancellationToken: cancellationToken);

    private Task EnsureWorkflowIndexesAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(
            _collections.WorkflowInstances.Indexes.CreateManyAsync(
                [
                    new CreateIndexModel<WorkflowInstance>(
                        Builders<WorkflowInstance>.IndexKeys
                            .Ascending(item => item.TenantId)
                            .Ascending(item => item.OperationId)
                            .Descending(item => item.UpdatedAtUtc))
                ],
                cancellationToken: cancellationToken),
            _collections.WorkflowPendingRequests.Indexes.CreateManyAsync(
                [
                    new CreateIndexModel<WorkflowPendingRequest>(
                        Builders<WorkflowPendingRequest>.IndexKeys
                            .Ascending(item => item.TenantId)
                            .Ascending(item => item.WorkflowInstanceId)
                            .Ascending(item => item.RequestId),
                        new CreateIndexOptions { Unique = true }),
                    new CreateIndexModel<WorkflowPendingRequest>(
                        Builders<WorkflowPendingRequest>.IndexKeys
                            .Ascending(item => item.TenantId)
                            .Ascending(item => item.OperationId)
                            .Ascending(item => item.Status)
                            .Descending(item => item.UpdatedAtUtc)),
                    new CreateIndexModel<WorkflowPendingRequest>(
                        Builders<WorkflowPendingRequest>.IndexKeys
                            .Ascending(item => item.TenantId)
                            .Ascending(item => item.WorkflowInstanceId)
                            .Descending(item => item.CreatedAtUtc))
                ],
                cancellationToken: cancellationToken));

    private Task EnsureCheckpointIndexesAsync(CancellationToken cancellationToken) =>
        _collections.WorkflowCheckpoints.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<WorkflowCheckpointDocument>(
                    Builders<WorkflowCheckpointDocument>.IndexKeys
                        .Ascending(item => item.SessionId)
                        .Ascending(item => item.CheckpointId),
                    new CreateIndexOptions { Unique = true }),
                new CreateIndexModel<WorkflowCheckpointDocument>(
                    Builders<WorkflowCheckpointDocument>.IndexKeys
                        .Ascending(item => item.SessionId)
                        .Ascending(item => item.ParentCheckpointId)
                        .Ascending(item => item.CreatedAtUtc))
            ],
            cancellationToken: cancellationToken);
}
