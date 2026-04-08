using ConversationalOrchestration.Api.Models;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace ConversationalOrchestration.Api.Controllers;

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
