using ConversationalOrchestration.Infrastructure.Configuration;
using ConversationalOrchestration.Infrastructure.Extensions;
using ConversationalOrchestration.FundAdministration.Extensions;
using ConversationalOrchestration.Api.Middleware;
using ConversationalOrchestration.Api.Models;

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

var app = builder.Build();

app.UseCors("frontend");
app.UseMiddleware<MockTenantMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();

app.Run();
