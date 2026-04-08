using ConversationalOrchestration.Infrastructure.Configuration;
using ConversationalOrchestration.Infrastructure.Extensions;
using ConversationalOrchestration.FundAdministration.Extensions;
using ConversationalOrchestration.FundAdministration.Messaging;
using MongoDB.Driver;
using NServiceBus;
using NServiceBus.Storage.MongoDB;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddConversationalOrchestrationInfrastructure(builder.Configuration);
builder.Services.AddFundAdministrationModule();

var endpointConfiguration = new EndpointConfiguration(FundAdministrationEndpointNames.Workflow);
endpointConfiguration.UseSerialization<SystemJsonSerializer>();
endpointConfiguration.EnableInstallers();
endpointConfiguration.SendFailedMessagesTo(FundAdministrationEndpointNames.Error);
endpointConfiguration.AuditProcessedMessagesTo(FundAdministrationEndpointNames.Audit);

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
