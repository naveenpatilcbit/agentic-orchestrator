using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.FundAdministration.Agents;
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
        services.AddScoped<IFundAdministrationWorkflowDispatcher, FundAdministrationWorkflowDispatcher>();
        services.AddScoped<IReviewContinuationHandler, FundOnboardingReviewContinuationHandler>();
        return services;
    }
}
