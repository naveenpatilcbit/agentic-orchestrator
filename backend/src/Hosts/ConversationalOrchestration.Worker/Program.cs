using ConversationalOrchestration.Infrastructure.Extensions;
using ConversationalOrchestration.FundAdministration.Extensions;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddConversationalOrchestrationInfrastructure(builder.Configuration);
builder.Services.AddFundAdministrationModule();

var host = builder.Build();
await host.RunAsync();
