using MongoDB.Driver;
using Microsoft.Extensions.Options;
using FundOrchestrator.Domain.Auditing;
using FundOrchestrator.Domain.Conversations;
using FundOrchestrator.Domain.Files;
using FundOrchestrator.Domain.Operations;
using FundOrchestrator.Domain.Reviews;
using FundOrchestrator.Infrastructure.Configuration;

namespace FundOrchestrator.Infrastructure.Data;

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

    public IMongoCollection<ReviewTask> ReviewTasks =>
        _database.GetCollection<ReviewTask>("review_tasks");

    public IMongoCollection<AuditEvent> AuditEvents =>
        _database.GetCollection<AuditEvent>("audit_events");

    public IMongoCollection<FileAsset> Files =>
        _database.GetCollection<FileAsset>("file_assets");
}
