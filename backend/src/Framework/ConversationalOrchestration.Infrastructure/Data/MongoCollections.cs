using MongoDB.Driver;
using Microsoft.Extensions.Options;
using ConversationalOrchestration.Domain.Auditing;
using ConversationalOrchestration.Domain.Conversations;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ConversationalOrchestration.Domain.Reviews;
using ConversationalOrchestration.Domain.Workflows;
using ConversationalOrchestration.Infrastructure.Configuration;

namespace ConversationalOrchestration.Infrastructure.Data;

public sealed class MongoCollections
{
    private readonly IMongoDatabase _database;

    public MongoCollections(IMongoClient mongoClient, IOptions<MongoDbOptions> options)
    {
        _database = mongoClient.GetDatabase(options.Value.DatabaseName);
    }

    public IMongoCollection<ConversationThread> Conversations =>
        _database.GetCollection<ConversationThread>("conversations");

    public IMongoCollection<ConversationMessage> Messages =>
        _database.GetCollection<ConversationMessage>("conversation_messages");

    public IMongoCollection<AgentOperation> Operations =>
        _database.GetCollection<AgentOperation>("operations");

    public IMongoCollection<OperationOutput> OperationOutputs =>
        _database.GetCollection<OperationOutput>("operation_outputs");

    public IMongoCollection<ReviewTask> ReviewTasks =>
        _database.GetCollection<ReviewTask>("review_tasks");

    public IMongoCollection<AuditEvent> AuditEvents =>
        _database.GetCollection<AuditEvent>("audit_events");

    public IMongoCollection<FileAsset> Files =>
        _database.GetCollection<FileAsset>("file_assets");

    public IMongoCollection<WorkflowInstance> WorkflowInstances =>
        _database.GetCollection<WorkflowInstance>("workflow_instances");

    public IMongoCollection<WorkflowPendingRequest> WorkflowPendingRequests =>
        _database.GetCollection<WorkflowPendingRequest>("workflow_pending_requests");

    public IMongoCollection<WorkflowCheckpointDocument> WorkflowCheckpoints =>
        _database.GetCollection<WorkflowCheckpointDocument>("workflow_checkpoints");
}
