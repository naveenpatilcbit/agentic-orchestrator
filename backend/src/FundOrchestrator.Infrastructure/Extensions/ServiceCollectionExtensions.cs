using FundOrchestrator.Application.Abstractions;
using FundOrchestrator.Application.Agents;
using FundOrchestrator.Application.Conversations;
using FundOrchestrator.Application.Operations;
using FundOrchestrator.Application.Reviews;
using FundOrchestrator.Contracts.Messaging;
using FundOrchestrator.Infrastructure.Configuration;
using FundOrchestrator.Infrastructure.Data;
using FundOrchestrator.Infrastructure.Files;
using FundOrchestrator.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NServiceBus;

namespace FundOrchestrator.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFundOrchestratorCore(this IServiceCollection services)
    {
        services.AddScoped<IAgent, NoticeCreationAgent>();
        services.AddScoped<IAgent, FundOnboardingAgent>();
        services.AddScoped<IAgent, OnePagerAgent>();
        services.AddScoped<IAgentCatalog, AgentCatalog>();
        services.AddScoped<IMessageRoutingService, MessageRoutingService>();
        services.AddScoped<IChatOrchestratorService, ChatOrchestratorService>();
        services.AddScoped<IReviewTaskService, ReviewTaskService>();
        return services;
    }

    public static IServiceCollection AddFundOrchestratorInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MongoDbOptions>(configuration.GetSection(MongoDbOptions.SectionName));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));

        services.AddSingleton<IMongoClient>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<MongoDbOptions>>().Value;
            return new MongoClient(options.ConnectionString);
        });

        services.AddSingleton<MongoCollections>();
        services.AddScoped<IConversationRepository, ConversationRepository>();
        services.AddScoped<IConversationMessageRepository, ConversationMessageRepository>();
        services.AddScoped<IAgentOperationRepository, AgentOperationRepository>();
        services.AddScoped<IReviewTaskRepository, ReviewTaskRepository>();
        services.AddScoped<IAuditEventRepository, AuditEventRepository>();
        services.AddScoped<IFileAssetRepository, FileAssetRepository>();
        services.AddScoped<IFileStorageService, LocalFileStorageService>();
        services.AddScoped<IWorkflowCommandDispatcher, WorkflowCommandDispatcher>();

        services.AddFundOrchestratorCore();
        return services;
    }
}

public sealed class WorkflowCommandDispatcher : IWorkflowCommandDispatcher
{
    private readonly IMessageSession _messageSession;

    public WorkflowCommandDispatcher(IMessageSession messageSession)
    {
        _messageSession = messageSession;
    }

    public Task StartOnboardingAsync(StartOnboardingWorkflowCommand command, CancellationToken cancellationToken) =>
        _messageSession.Send(command, cancellationToken);

    public Task AdvanceOnboardingReviewAsync(AdvanceOnboardingReviewCommand command, CancellationToken cancellationToken) =>
        _messageSession.Send(command, cancellationToken);
}
