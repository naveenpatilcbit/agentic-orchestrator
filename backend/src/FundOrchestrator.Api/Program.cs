using FundOrchestrator.Contracts.Messaging;
using FundOrchestrator.Infrastructure.Configuration;
using FundOrchestrator.Infrastructure.Extensions;
using FundOrchestrator.Api.Middleware;
using FundOrchestrator.Api.Models;
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
builder.Services.AddFundOrchestratorInfrastructure(builder.Configuration);

builder.Host.UseNServiceBus(context =>
{
    var endpointConfiguration = new EndpointConfiguration(EndpointNames.Api);
    endpointConfiguration.SendOnly();
    endpointConfiguration.UseSerialization<SystemJsonSerializer>();

    var transport = endpointConfiguration.UseTransport<RabbitMQTransport>();
    transport.UseConventionalRoutingTopology(QueueType.Quorum);
    transport.ConnectionString(context.Configuration.GetConnectionString("RabbitMq"));

    var routing = transport.Routing();
    routing.RouteToEndpoint(typeof(StartOnboardingWorkflowCommand), EndpointNames.Workflow);
    routing.RouteToEndpoint(typeof(AdvanceOnboardingReviewCommand), EndpointNames.Workflow);

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
