using FundOrchestrator.Application.Abstractions;
using FundOrchestrator.Application.Support;
using FundOrchestrator.Domain.Files;
using FundOrchestrator.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace FundOrchestrator.Infrastructure.Files;

public sealed class LocalFileStorageService : IFileStorageService
{
    private readonly IFileAssetRepository _fileAssetRepository;
    private readonly StorageOptions _options;

    public LocalFileStorageService(IFileAssetRepository fileAssetRepository, IOptions<StorageOptions> options)
    {
        _fileAssetRepository = fileAssetRepository;
        _options = options.Value;
    }

    public async Task<FileAsset> SaveAsync(
        Stream stream,
        string fileName,
        string contentType,
        string conversationId,
        TenantExecutionContext context,
        CancellationToken cancellationToken)
    {
        var safeFileName = $"{Guid.NewGuid():N}-{Path.GetFileName(fileName)}";
        var tenantRoot = Path.Combine(_options.UploadsRoot, context.TenantId, conversationId);
        Directory.CreateDirectory(tenantRoot);

        var fullPath = Path.Combine(tenantRoot, safeFileName);
        await using (var output = File.Create(fullPath))
        {
            await stream.CopyToAsync(output, cancellationToken);
        }

        var asset = new FileAsset
        {
            TenantId = context.TenantId,
            ConversationId = conversationId,
            FileName = fileName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            RelativePath = fullPath,
            SizeBytes = new FileInfo(fullPath).Length
        };

        await _fileAssetRepository.AddAsync(asset, cancellationToken);
        return asset;
    }
}
