using FundOrchestrator.Api.Models;
using FundOrchestrator.Application.Support;

namespace FundOrchestrator.Api.Middleware;

public sealed class MockTenantMiddleware
{
    private readonly RequestDelegate _next;

    public MockTenantMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext httpContext, RequestContextAccessor accessor)
    {
        var tenantId = httpContext.Request.Headers["X-Tenant-Id"].FirstOrDefault() ?? "tenant-demo";
        var userId = httpContext.Request.Headers["X-User-Id"].FirstOrDefault() ?? "user-demo";
        var displayName = httpContext.Request.Headers["X-User-Name"].FirstOrDefault() ?? "Demo User";

        accessor.Set(new TenantExecutionContext(tenantId, userId, displayName));
        await _next(httpContext);
    }
}
