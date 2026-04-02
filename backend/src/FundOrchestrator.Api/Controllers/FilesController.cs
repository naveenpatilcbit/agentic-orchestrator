using FundOrchestrator.Api.Models;
using FundOrchestrator.Application.Abstractions;
using FundOrchestrator.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace FundOrchestrator.Api.Controllers;

[ApiController]
[Route("api/files")]
public sealed class FilesController : ControllerBase
{
    private readonly IFileStorageService _fileStorageService;
    private readonly RequestContextAccessor _requestContextAccessor;

    public FilesController(IFileStorageService fileStorageService, RequestContextAccessor requestContextAccessor)
    {
        _fileStorageService = fileStorageService;
        _requestContextAccessor = requestContextAccessor;
    }

    [HttpPost("upload")]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult<IReadOnlyCollection<FileAssetDto>>> UploadAsync(
        [FromForm] string conversationId,
        [FromForm] List<IFormFile> files,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return BadRequest("conversationId is required for uploads.");
        }

        var stored = new List<FileAssetDto>(files.Count);

        foreach (var file in files)
        {
            await using var stream = file.OpenReadStream();
            var asset = await _fileStorageService.SaveAsync(
                stream,
                file.FileName,
                file.ContentType,
                conversationId,
                _requestContextAccessor.Current,
                cancellationToken);

            stored.Add(new FileAssetDto(
                asset.Id,
                asset.ConversationId,
                asset.FileName,
                asset.ContentType,
                asset.SizeBytes,
                asset.UploadedAtUtc));
        }

        return Ok(stored);
    }
}
