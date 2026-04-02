using FundOrchestrator.Api.Models;
using FundOrchestrator.Application.Abstractions;
using FundOrchestrator.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace FundOrchestrator.Api.Controllers;

[ApiController]
[Route("api/chat")]
public sealed class ChatController : ControllerBase
{
    private readonly IChatOrchestratorService _chatOrchestratorService;
    private readonly RequestContextAccessor _requestContextAccessor;

    public ChatController(IChatOrchestratorService chatOrchestratorService, RequestContextAccessor requestContextAccessor)
    {
        _chatOrchestratorService = chatOrchestratorService;
        _requestContextAccessor = requestContextAccessor;
    }

    [HttpPost("messages")]
    public async Task<ActionResult<ConversationSnapshotResponse>> PostMessageAsync(
        [FromBody] ChatMessageRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _chatOrchestratorService.HandleMessageAsync(request, _requestContextAccessor.Current, cancellationToken);
        return Ok(response);
    }

    [HttpGet("conversations/{conversationId}")]
    public async Task<ActionResult<ConversationSnapshotResponse>> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken)
    {
        var snapshot = await _chatOrchestratorService.GetSnapshotAsync(conversationId, _requestContextAccessor.Current, cancellationToken);
        return snapshot is null ? NotFound() : Ok(snapshot);
    }

    [HttpGet("conversations")]
    public async Task<ActionResult<IReadOnlyCollection<ConversationSummaryDto>>> ListConversationsAsync(
        CancellationToken cancellationToken)
    {
        var conversations = await _chatOrchestratorService.ListConversationsAsync(_requestContextAccessor.Current, cancellationToken);
        return Ok(conversations);
    }
}
