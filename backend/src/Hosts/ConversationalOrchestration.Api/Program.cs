using ConversationalOrchestration.Infrastructure.Configuration;
using ConversationalOrchestration.Infrastructure.Extensions;
using ConversationalOrchestration.FundAdministration.Extensions;
using ConversationalOrchestration.FundAdministration.Messaging;
using ConversationalOrchestration.Api.Middleware;
using ConversationalOrchestration.Api.Models;
using NServiceBus;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
        policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowAnyOrigin());
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<RequestContextAccessor>();
builder.Services.AddConversationalOrchestrationInfrastructure(builder.Configuration);
builder.Services.AddFundAdministrationModule();

builder.Host.UseNServiceBus(context =>
{
    var endpointConfiguration = new EndpointConfiguration(FundAdministrationEndpointNames.Api);
    endpointConfiguration.SendOnly();
    endpointConfiguration.UseSerialization<SystemJsonSerializer>();

    var transport = endpointConfiguration.UseTransport<RabbitMQTransport>();
    transport.UseConventionalRoutingTopology(QueueType.Quorum);
    transport.ConnectionString(context.Configuration.GetConnectionString("RabbitMq"));

    var routing = transport.Routing();
    routing.RouteToEndpoint(typeof(StartFundOnboardingCommand), FundAdministrationEndpointNames.Workflow);
    routing.RouteToEndpoint(typeof(ContinueFundOnboardingReviewCommand), FundAdministrationEndpointNames.Workflow);

    return endpointConfiguration;
});

var app = builder.Build();

app.UseCors("frontend");
app.UseMiddleware<MockTenantMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();

app.Run();
