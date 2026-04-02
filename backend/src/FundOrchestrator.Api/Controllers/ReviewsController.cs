using FundOrchestrator.Api.Models;
using FundOrchestrator.Application.Abstractions;
using FundOrchestrator.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace FundOrchestrator.Api.Controllers;

[ApiController]
[Route("api/reviews")]
public sealed class ReviewsController : ControllerBase
{
    private readonly IReviewTaskService _reviewTaskService;
    private readonly IChatOrchestratorService _chatOrchestratorService;
    private readonly RequestContextAccessor _requestContextAccessor;

    public ReviewsController(
        IReviewTaskService reviewTaskService,
        IChatOrchestratorService chatOrchestratorService,
        RequestContextAccessor requestContextAccessor)
    {
        _reviewTaskService = reviewTaskService;
        _chatOrchestratorService = chatOrchestratorService;
        _requestContextAccessor = requestContextAccessor;
    }

    [HttpPost("{reviewTaskId}/decision")]
    public async Task<ActionResult<ConversationSnapshotResponse>> SubmitDecisionAsync(
        string reviewTaskId,
        [FromBody] ReviewDecisionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _reviewTaskService.SubmitAsync(reviewTaskId, request, _requestContextAccessor.Current, cancellationToken);
        var snapshot = await _chatOrchestratorService.GetSnapshotAsync(result.ConversationId, _requestContextAccessor.Current, cancellationToken);
        return snapshot is null ? NotFound() : Ok(snapshot);
    }
}
