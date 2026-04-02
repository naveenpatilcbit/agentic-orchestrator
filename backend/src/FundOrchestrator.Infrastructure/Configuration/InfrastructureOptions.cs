namespace FundOrchestrator.Infrastructure.Configuration;

public sealed class MongoDbOptions
{
    public const string SectionName = "Mongo";

    public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    public string DatabaseName { get; set; } = "fund_orchestrator";
    public string MessagingDatabaseName { get; set; } = "fund_orchestrator_nservicebus";
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string UploadsRoot { get; set; } = "uploads";
}
