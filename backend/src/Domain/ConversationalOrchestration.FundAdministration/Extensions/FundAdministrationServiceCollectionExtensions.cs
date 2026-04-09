using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.FundAdministration.Agents;
using ConversationalOrchestration.FundAdministration.CapitalCalls;
using ConversationalOrchestration.FundAdministration.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace ConversationalOrchestration.FundAdministration.Extensions;

public static class FundAdministrationServiceCollectionExtensions
{
    public static IServiceCollection AddFundAdministrationModule(this IServiceCollection services)
    {
        services.AddScoped<IAgent, NoticeCreationAgent>();
        services.AddScoped<IAgent, FundOnboardingAgent>();
        services.AddScoped<IAgent, OnePagerAgent>();
        services.AddScoped<ICapitalCallConversationIntelligence, CapitalCallConversationIntelligence>();
        services.AddScoped<ICapitalCallRequestPreparationService, CapitalCallRequestPreparationService>();
        services.AddScoped<ICapitalCallAllocationEngine, CapitalCallAllocationEngine>();
        services.AddScoped<ICapitalCallResultMaterializer, CapitalCallResultMaterializer>();
        services.AddScoped<ICapitalCallDataProvider, CsvAttachmentCapitalCallDataProvider>();
        services.AddSingleton<ICapitalCallDataProvider, InMemorySaaSCapitalCallDataProvider>();
        services.AddSingleton<IFxRateProvider, SampleFxRateProvider>();
        services.AddSingleton<CapitalCallWorkflowTools>();
        services.AddSingleton<CapitalCallWorkflowResponseAgentProvider>();
        services.AddSingleton<IWorkflowDefinition, CapitalCallNoticeWorkflowDefinition>();
        services.AddSingleton<IWorkflowDefinition, FundOnboardingWorkflowDefinition>();
        services.AddScoped<IFundAdministrationWorkflowDispatcher, FundAdministrationWorkflowDispatcher>();
        services.AddScoped<IReviewContinuationHandler, FundOnboardingReviewContinuationHandler>();
        return services;
    }
}
