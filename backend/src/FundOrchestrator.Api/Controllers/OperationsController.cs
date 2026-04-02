using FundOrchestrator.Api.Models;
using FundOrchestrator.Application.Abstractions;
using FundOrchestrator.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace FundOrchestrator.Api.Controllers;

[ApiController]
[Route("api/operations")]
public sealed class OperationsController : ControllerBase
{
    private readonly IChatOrchestratorService _chatOrchestratorService;
    private readonly RequestContextAccessor _requestContextAccessor;

    public OperationsController(
        IChatOrchestratorService chatOrchestratorService,
        RequestContextAccessor requestContextAccessor)
    {
        _chatOrchestratorService = chatOrchestratorService;
        _requestContextAccessor = requestContextAccessor;
    }

    [HttpGet("conversation/{conversationId}")]
    public async Task<ActionResult<IReadOnlyCollection<AgentOperationDto>>> ListByConversationAsync(
        string conversationId,
        CancellationToken cancellationToken)
    {
        var snapshot = await _chatOrchestratorService.GetSnapshotAsync(conversationId, _requestContextAccessor.Current, cancellationToken);
        return snapshot is null ? NotFound() : Ok(snapshot.Operations);
    }
}
