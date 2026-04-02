using FundOrchestrator.Contracts.Messaging;
using FundOrchestrator.Infrastructure.Configuration;
using FundOrchestrator.Infrastructure.Extensions;
using MongoDB.Driver;
using NServiceBus;
using NServiceBus.Storage.MongoDB;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddFundOrchestratorInfrastructure(builder.Configuration);

var endpointConfiguration = new EndpointConfiguration(EndpointNames.Workflow);
endpointConfiguration.UseSerialization<SystemJsonSerializer>();
endpointConfiguration.EnableInstallers();
endpointConfiguration.SendFailedMessagesTo("fund-orchestrator-error");
endpointConfiguration.AuditProcessedMessagesTo("fund-orchestrator-audit");

var transport = endpointConfiguration.UseTransport<RabbitMQTransport>();
transport.UseConventionalRoutingTopology(QueueType.Quorum);
transport.ConnectionString(builder.Configuration.GetConnectionString("RabbitMq"));

var persistence = endpointConfiguration.UsePersistence<MongoPersistence>();
var mongoOptions = builder.Configuration.GetSection(MongoDbOptions.SectionName).Get<MongoDbOptions>() ?? new MongoDbOptions();
persistence.MongoClient(new MongoClient(mongoOptions.ConnectionString));
persistence.DatabaseName(mongoOptions.MessagingDatabaseName);
persistence.UseTransactions(false);

builder.UseNServiceBus(endpointConfiguration);

var host = builder.Build();
await host.RunAsync();
