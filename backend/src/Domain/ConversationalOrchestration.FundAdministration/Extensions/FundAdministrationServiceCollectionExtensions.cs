using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.FundAdministration.Agents;
using ConversationalOrchestration.FundAdministration.CapitalCalls;
using ConversationalOrchestration.FundAdministration.CapitalCalls.Demo;
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
        services.AddScoped<IAgent, TemplateOutputAgent>();
        services.AddScoped<ICapitalCallConversationIntelligence, CapitalCallConversationIntelligence>();
        services.AddScoped<ICapitalCallRequestPreparationService, CapitalCallRequestPreparationService>();
        services.AddScoped<ICapitalCallExtractionReviewService, CapitalCallExtractionReviewService>();
        services.AddScoped<ICapitalCallAllocationEngine, CapitalCallAllocationEngine>();
        services.AddScoped<ICapitalCallReviewArtifactService, CapitalCallReviewArtifactService>();
        services.AddScoped<ITemplateOutputGenerationService, TemplateOutputGenerationService>();
        services.AddSingleton<ICapitalCallProviderConfigurationService, InMemoryCapitalCallProviderConfigurationService>();
        services.AddScoped<ICapitalCallDataProvider, CsvAttachmentCapitalCallDataProvider>();
        services.AddSingleton<ICapitalCallDataProvider, InMemorySaaSCapitalCallDataProvider>();
        services.AddSingleton<IFxRateProvider, SampleFxRateProvider>();
        services.AddScoped<ITemplateOutputSourceHandler, CapitalCallTemplateOutputSourceHandler>();
        services.AddSingleton<IWorkflowDefinition, CapitalCallNoticeWorkflowDefinition>();
        services.AddSingleton<IWorkflowDefinition, FundOnboardingWorkflowDefinition>();
        services.AddScoped<IFundAdministrationWorkflowDispatcher, FundAdministrationWorkflowDispatcher>();
        services.AddScoped<IReviewContinuationHandler, CapitalCallReviewContinuationHandler>();
        services.AddScoped<IReviewContinuationHandler, FundOnboardingReviewContinuationHandler>();
        return services;
    }
}
