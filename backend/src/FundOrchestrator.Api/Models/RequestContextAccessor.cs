using FundOrchestrator.Application.Support;

namespace FundOrchestrator.Api.Models;

public sealed class RequestContextAccessor
{
    public TenantExecutionContext Current { get; private set; } = new("tenant-demo", "user-demo", "Demo User");

    public void Set(TenantExecutionContext context)
    {
        Current = context;
    }
}
