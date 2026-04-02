namespace FundOrchestrator.Application.Support;

public sealed record TenantExecutionContext(
    string TenantId,
    string UserId,
    string DisplayName);
