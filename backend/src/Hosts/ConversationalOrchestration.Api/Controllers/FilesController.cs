using ConversationalOrchestration.Api.Models;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Contracts;
using ConversationalOrchestration.Domain.Files;
using Microsoft.AspNetCore.Mvc;

namespace ConversationalOrchestration.Api.Controllers;

[ApiController]
[Route("api/files")]
public sealed class FilesController : ControllerBase
{
    private readonly IFileStorageService _fileStorageService;
    private readonly IFileAssetRepository _fileAssetRepository;
    private readonly RequestContextAccessor _requestContextAccessor;

    public FilesController(
        IFileStorageService fileStorageService,
        IFileAssetRepository fileAssetRepository,
        RequestContextAccessor requestContextAccessor)
    {
        _fileStorageService = fileStorageService;
        _fileAssetRepository = fileAssetRepository;
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
                FileAssetKind.UploadedInput,
                _requestContextAccessor.Current,
                cancellationToken);

            stored.Add(new FileAssetDto(
                asset.Id,
                asset.ConversationId,
                asset.FileName,
                asset.ContentType,
                asset.Kind.ToString(),
                asset.SizeBytes,
                asset.UploadedAtUtc));
        }

        return Ok(stored);
    }

    [HttpGet("{fileAssetId}/download")]
    public async Task<IActionResult> DownloadAsync(
        string fileAssetId,
        CancellationToken cancellationToken)
    {
        var asset = await _fileAssetRepository.GetAsync(fileAssetId, _requestContextAccessor.Current.TenantId, cancellationToken);
        if (asset is null || !System.IO.File.Exists(asset.RelativePath))
        {
            return NotFound();
        }

        var stream = System.IO.File.OpenRead(asset.RelativePath);
        return File(stream, asset.ContentType, asset.FileName);
    }
}
