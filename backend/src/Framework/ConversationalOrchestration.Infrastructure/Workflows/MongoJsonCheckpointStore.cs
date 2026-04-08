using ConversationalOrchestration.Domain.Workflows;
using ConversationalOrchestration.Infrastructure.Data;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using MongoDB.Driver;
using System.Text.Json;

namespace ConversationalOrchestration.Infrastructure.Workflows;

public sealed class MongoJsonCheckpointStore : ICheckpointStore<JsonElement>
{
    private readonly MongoCollections _collections;

    public MongoJsonCheckpointStore(MongoCollections collections)
    {
        _collections = collections;
    }

    public async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent)
    {
        var filter = Builders<WorkflowCheckpointDocument>.Filter.Eq(item => item.SessionId, sessionId);
        if (withParent is not null)
        {
            filter &= Builders<WorkflowCheckpointDocument>.Filter.Eq(item => item.ParentCheckpointId, withParent.CheckpointId);
        }

        var documents = await _collections.WorkflowCheckpoints.Find(filter)
            .SortBy(item => item.CreatedAtUtc)
            .ToListAsync();

        return documents.Select(document => new CheckpointInfo(sessionId, document.CheckpointId));
    }

    public async ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent)
    {
        var checkpoint = new CheckpointInfo(sessionId, Guid.NewGuid().ToString("N"));
        var document = new WorkflowCheckpointDocument
        {
            SessionId = sessionId,
            CheckpointId = checkpoint.CheckpointId,
            ParentCheckpointId = parent?.CheckpointId,
            PayloadJson = value.GetRawText()
        };

        await _collections.WorkflowCheckpoints.InsertOneAsync(document);
        return checkpoint;
    }

    public async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        var document = await _collections.WorkflowCheckpoints.Find(item =>
                item.SessionId == sessionId &&
                item.CheckpointId == key.CheckpointId)
            .FirstOrDefaultAsync();

        if (document is null)
        {
            throw new InvalidOperationException($"Checkpoint '{key.CheckpointId}' was not found for workflow session '{sessionId}'.");
        }

        using var payload = JsonDocument.Parse(document.PayloadJson);
        return payload.RootElement.Clone();
    }
}
