using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Agents;
using ConversationalOrchestration.Application.Conversations;
using ConversationalOrchestration.Application.Operations;
using ConversationalOrchestration.Application.Reviews;
using ConversationalOrchestration.Infrastructure.AI;
using ConversationalOrchestration.Infrastructure.Configuration;
using ConversationalOrchestration.Infrastructure.Data;
using ConversationalOrchestration.Infrastructure.Files;
using ConversationalOrchestration.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace ConversationalOrchestration.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConversationalOrchestrationCore(this IServiceCollection services)
    {
        services.AddScoped<IAgentCatalog, AgentCatalog>();
        services.AddScoped<IMessageRoutingService, MessageRoutingService>();
        services.AddScoped<IChatOrchestratorService, ChatOrchestratorService>();
        services.AddScoped<IReviewTaskService, ReviewTaskService>();
        return services;
    }

    public static IServiceCollection AddConversationalOrchestrationInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MongoDbOptions>(configuration.GetSection(MongoDbOptions.SectionName));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.Configure<LlmGatewayOptions>(configuration.GetSection(LlmGatewayOptions.SectionName));

        services.AddSingleton<IMongoClient>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MongoDbOptions>>().Value;
            return new MongoClient(options.ConnectionString);
        });

        services.AddSingleton<IStructuredLlmClient, MicrosoftExtensionsAiStructuredLlmClient>();
        services.AddScoped<IMessageIntentClassifier, LlmMessageIntentClassifier>();
        services.AddScoped<IAgentInputCompletionService, LlmAgentInputCompletionService>();
        services.AddSingleton<MongoCollections>();
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IConversationMessageRepository, ConversationMessageRepository>();
        services.AddScoped<IAgentOperationRepository, AgentOperationRepository>();
        services.AddScoped<IReviewTaskRepository, ReviewTaskRepository>();
        services.AddScoped<IAuditEventRepository, AuditEventRepository>();
        services.AddScoped<IFileAssetRepository, FileAssetRepository>();
        services.AddScoped<IFileStorageService, LocalFileStorageService>();

        services.AddConversationalOrchestrationCore();
        return services;
    }
}
