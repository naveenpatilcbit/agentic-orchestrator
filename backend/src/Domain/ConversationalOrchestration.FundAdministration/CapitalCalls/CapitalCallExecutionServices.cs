using System.Globalization;
using System.Text;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;

namespace ConversationalOrchestration.FundAdministration.CapitalCalls;

public interface ICapitalCallRequestPreparationService
{
    Task<CapitalCallPreparationResult> PrepareAsync(
        string tenantId,
        string conversationId,
        string currentStateJson,
        CapitalCallIntentPatch? patch,
        CancellationToken cancellationToken);
}

public interface ICapitalCallAllocationEngine
{
    Task<CapitalCallComputationResult> ComputeAsync(
        string tenantId,
        string conversationId,
        string requestStateJson,
        CancellationToken cancellationToken);
}

public interface ICapitalCallResultMaterializer
{
    Task<CapitalCallMaterializationResult> MaterializeAsync(
        string tenantId,
        string conversationId,
        string operationId,
        string requestStateJson,
        string noticeDtoJson,
        CancellationToken cancellationToken);
}

public interface ICapitalCallDataProvider
{
    bool CanHandle(CapitalCallExecutionProfile profile);

    Task<CapitalCallFundSnapshot?> ResolveRootFundAsync(
        string tenantId,
        string conversationId,
        CapitalCallRequestState state,
        CancellationToken cancellationToken);

    Task<CapitalCallFundSnapshot?> GetFundAsync(
        string tenantId,
        string conversationId,
        CapitalCallRequestState state,
        string fundId,
        CancellationToken cancellationToken);
}

public interface IFxRateProvider
{
    Task<decimal> ConvertAsync(
        decimal amount,
        string fromCurrency,
        string toCurrency,
        CancellationToken cancellationToken);
}

public interface ICapitalCallProviderConfigurationService
{
    Task<CapitalCallProviderConfiguration> ResolveAsync(
        string tenantId,
        CancellationToken cancellationToken);
}

public sealed class CapitalCallRequestPreparationService : ICapitalCallRequestPreparationService
{
    private static readonly IReadOnlyDictionary<string, string> AttachmentPromptSamples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["csv"] = "FundName,FundCurrency,PartnerName,CommitmentPercentage,PartnerCurrency,PartnerType,ChildFundName"
    };

    private readonly IFileAssetRepository _fileAssetRepository;
    private readonly IReadOnlyCollection<ICapitalCallDataProvider> _dataProviders;
    private readonly ICapitalCallProviderConfigurationService _providerConfigurationService;
    private readonly ILogger<CapitalCallRequestPreparationService> _logger;

    public CapitalCallRequestPreparationService(
        IFileAssetRepository fileAssetRepository,
        IEnumerable<ICapitalCallDataProvider> dataProviders,
        ICapitalCallProviderConfigurationService providerConfigurationService,
        ILogger<CapitalCallRequestPreparationService> logger)
    {
        _fileAssetRepository = fileAssetRepository;
        _dataProviders = dataProviders.ToArray();
        _providerConfigurationService = providerConfigurationService;
        _logger = logger;
    }

    public async Task<CapitalCallPreparationResult> PrepareAsync(
        string tenantId,
        string conversationId,
        string currentStateJson,
        CapitalCallIntentPatch? patch,
        CancellationToken cancellationToken)
    {
        var state = LoadState(currentStateJson);
        MergePatch(state, patch);

        var attachments = await _fileAssetRepository.ListByConversationAsync(conversationId, tenantId, cancellationToken);
        var providerConfiguration = await _providerConfigurationService.ResolveAsync(tenantId, cancellationToken);
        var sourceResolutionIssue = ResolveExecutionProfile(state, attachments, providerConfiguration);
        _logger.LogInformation(
            "Preparing capital call request for tenant {TenantId} conversation {ConversationId}. Profile={Profile} ProviderName={ProviderName} FundName={FundName} Amount={Amount} AttachmentCount={AttachmentCount} OverrideCount={OverrideCount}",
            tenantId,
            conversationId,
            state.Profile,
            providerConfiguration.ProviderName,
            state.FundName,
            state.CapitalCallAmount,
            attachments.Count,
            state.PartnerOverrides.Count);

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(state.FundName))
        {
            missing.Add("fund name");
        }

        if (!state.CapitalCallAmount.HasValue || state.CapitalCallAmount.Value <= 0)
        {
            missing.Add("capital call amount");
        }

        if (missing.Count > 0)
        {
            _logger.LogInformation(
                "Capital call request is missing inputs for tenant {TenantId} conversation {ConversationId}. Missing={MissingFields}",
                tenantId,
                conversationId,
                string.Join(", ", missing));
            return new CapitalCallPreparationResult
            {
                IsReady = false,
                RequestState = state,
                RequestStateJson = JsonContent.Serialize(state),
                ClarificationPrompt = $"I still need the {string.Join(" and ", missing)} before I can calculate the capital call allocations.",
                Summary = "Waiting for the remaining capital call inputs."
            };
        }

        if (!string.IsNullOrWhiteSpace(sourceResolutionIssue))
        {
            _logger.LogInformation(
                "Capital call request requires a source input for tenant {TenantId} conversation {ConversationId}. Issue={Issue}",
                tenantId,
                conversationId,
                sourceResolutionIssue);
            return new CapitalCallPreparationResult
            {
                IsReady = false,
                RequestState = state,
                RequestStateJson = JsonContent.Serialize(state),
                ClarificationPrompt = sourceResolutionIssue,
                Summary = "Waiting for a configured provider or uploaded source file."
            };
        }

        if (state.Profile == CapitalCallExecutionProfile.AttachmentFile &&
            !string.IsNullOrWhiteSpace(state.SourceAttachmentName) &&
            !Path.GetExtension(state.SourceAttachmentName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Unsupported attachment type for capital call request in tenant {TenantId} conversation {ConversationId}. Attachment={AttachmentName}",
                tenantId,
                conversationId,
                state.SourceAttachmentName);
            return new CapitalCallPreparationResult
            {
                IsReady = false,
                RequestState = state,
                RequestStateJson = JsonContent.Serialize(state),
                ClarificationPrompt = $"I found the attachment '{state.SourceAttachmentName}', but this demo expects a CSV export for attachment-driven capital calls. Use a file with columns like {AttachmentPromptSamples["csv"]}.",
                Summary = "Waiting for a CSV attachment with partner commitment data."
            };
        }

        var provider = ResolveProvider(state.Profile);
        var rootFund = await provider.ResolveRootFundAsync(tenantId, conversationId, state, cancellationToken);
        if (rootFund is null)
        {
            _logger.LogWarning(
                "Capital call root fund could not be resolved for tenant {TenantId} conversation {ConversationId}. FundName={FundName} Profile={Profile}",
                tenantId,
                conversationId,
                state.FundName,
                state.Profile);
            return new CapitalCallPreparationResult
            {
                IsReady = false,
                RequestState = state,
                RequestStateJson = JsonContent.Serialize(state),
                ClarificationPrompt = state.Profile == CapitalCallExecutionProfile.AttachmentFile
                    ? $"I couldn't find a fund named '{state.FundName}' in the uploaded source file. Please confirm the fund name or upload a corrected CSV."
                    : $"I couldn't find a fund named '{state.FundName}' in the configured provider. Please upload a CSV file with partner names, feeder details, and commitment percentages so I can calculate the capital call notice.",
                Summary = "Waiting for a valid fund reference."
            };
        }

        state.FundId = rootFund.FundId;
        state.FundName = rootFund.FundName;

        var validationIssue = await ValidateFundTreeAsync(
            tenantId,
            conversationId,
            state,
            provider,
            rootFund,
            [],
            0,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(validationIssue))
        {
            _logger.LogWarning(
                "Capital call validation failed for tenant {TenantId} conversation {ConversationId}. FundId={FundId} Issue={ValidationIssue}",
                tenantId,
                conversationId,
                state.FundId,
                validationIssue);
            return new CapitalCallPreparationResult
            {
                IsReady = false,
                RequestState = state,
                RequestStateJson = JsonContent.Serialize(state),
                ClarificationPrompt = validationIssue,
                Summary = "Waiting for clarification before the allocation engine can continue."
            };
        }

        _logger.LogInformation(
            "Capital call request is ready for tenant {TenantId} conversation {ConversationId}. FundId={FundId} FundName={FundName} Currency={Currency} Amount={Amount}",
            tenantId,
            conversationId,
            state.FundId,
            rootFund.FundName,
            rootFund.Currency,
            state.CapitalCallAmount);
        return new CapitalCallPreparationResult
        {
            IsReady = true,
            RequestState = state,
            RequestStateJson = JsonContent.Serialize(state),
            Summary = $"Ready to calculate {CapitalCallFormatting.FormatAmount(state.CapitalCallAmount!.Value, rootFund.Currency)} for {rootFund.FundName}."
        };
    }

    private async Task<string?> ValidateFundTreeAsync(
        string tenantId,
        string conversationId,
        CapitalCallRequestState state,
        ICapitalCallDataProvider provider,
        CapitalCallFundSnapshot fund,
        IReadOnlyCollection<string> path,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth >= 10)
        {
            _logger.LogWarning(
                "Capital call validation hit feeder depth limit for tenant {TenantId} conversation {ConversationId}. FundId={FundId} FundName={FundName} Depth={Depth}",
                tenantId,
                conversationId,
                fund.FundId,
                fund.FundName,
                depth);
            return $"The feeder structure under {fund.FundName} is deeper than the supported limit of 10 levels. Please review the fund hierarchy.";
        }

        if (path.Contains(fund.FundId, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Capital call validation detected feeder cycle for tenant {TenantId} conversation {ConversationId}. FundId={FundId} FundName={FundName}",
                tenantId,
                conversationId,
                fund.FundId,
                fund.FundName);
            return $"I detected a feeder cycle while expanding {fund.FundName}. Please review the fund hierarchy before retrying.";
        }

        if (fund.Partners.Count == 0)
        {
            _logger.LogWarning(
                "Capital call validation found no partners for tenant {TenantId} conversation {ConversationId}. FundId={FundId} FundName={FundName}",
                tenantId,
                conversationId,
                fund.FundId,
                fund.FundName);
            return $"The fund {fund.FundName} does not have any partner commitment data yet.";
        }

        var totalPercentage = fund.Partners.Sum(partner => ResolveCommitmentPercentage(partner, state));
        if (decimal.Round(totalPercentage, 2, MidpointRounding.AwayFromZero) != 100m)
        {
            _logger.LogWarning(
                "Capital call validation found invalid commitment total for tenant {TenantId} conversation {ConversationId}. FundId={FundId} FundName={FundName} TotalPercentage={TotalPercentage}",
                tenantId,
                conversationId,
                fund.FundId,
                fund.FundName,
                totalPercentage);
            return $"The partner commitment percentages for {fund.FundName} add up to {totalPercentage:N2}%. Please correct them before I continue.";
        }

        foreach (var feeder in fund.Partners.Where(partner => partner.Kind == CapitalCallParticipantKind.FeederFund))
        {
            if (string.IsNullOrWhiteSpace(feeder.ChildFundId))
            {
                _logger.LogWarning(
                    "Capital call validation found feeder without child fund for tenant {TenantId} conversation {ConversationId}. FundId={FundId} PartnerName={PartnerName}",
                    tenantId,
                    conversationId,
                    fund.FundId,
                    feeder.PartnerName);
                return $"The feeder partner {feeder.PartnerName} in {fund.FundName} does not point to a child fund.";
            }

            var childFund = await provider.GetFundAsync(tenantId, conversationId, state, feeder.ChildFundId, cancellationToken);
            if (childFund is null)
            {
                _logger.LogWarning(
                    "Capital call validation could not load child fund for tenant {TenantId} conversation {ConversationId}. ParentFundId={FundId} PartnerName={PartnerName} ChildFundId={ChildFundId}",
                    tenantId,
                    conversationId,
                    fund.FundId,
                    feeder.PartnerName,
                    feeder.ChildFundId);
                return $"I couldn't load the child feeder fund for {feeder.PartnerName}.";
            }

            var issue = await ValidateFundTreeAsync(
                tenantId,
                conversationId,
                state,
                provider,
                childFund,
                [..path, fund.FundId],
                depth + 1,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(issue))
            {
                return issue;
            }
        }

        return null;
    }

    private ICapitalCallDataProvider ResolveProvider(CapitalCallExecutionProfile profile) =>
        _dataProviders.FirstOrDefault(provider => provider.CanHandle(profile))
        ?? throw new InvalidOperationException($"No capital call data provider is registered for profile '{profile}'.");

    private static CapitalCallRequestState LoadState(string currentStateJson) =>
        JsonContent.Deserialize<CapitalCallRequestState>(currentStateJson) ?? new CapitalCallRequestState();

    private static void MergePatch(CapitalCallRequestState state, CapitalCallIntentPatch? patch)
    {
        if (patch is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(patch.FundName))
        {
            state.FundName = patch.FundName.Trim();
        }

        if (patch.CapitalCallAmount.HasValue)
        {
            state.CapitalCallAmount = patch.CapitalCallAmount.Value;
        }

        if (!string.IsNullOrWhiteSpace(patch.NoticeDate))
        {
            state.NoticeDate = patch.NoticeDate.Trim();
        }

        foreach (var overridePatch in patch.PartnerOverrides)
        {
            if (string.IsNullOrWhiteSpace(overridePatch.PartnerName) || !overridePatch.CommitmentPercentage.HasValue)
            {
                continue;
            }

            var existing = state.PartnerOverrides.FirstOrDefault(item =>
                string.Equals(item.PartnerName, overridePatch.PartnerName, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                state.PartnerOverrides.Add(new CapitalCallPartnerOverride
                {
                    PartnerName = overridePatch.PartnerName.Trim(),
                    CommitmentPercentage = overridePatch.CommitmentPercentage
                });
                continue;
            }

            existing.CommitmentPercentage = overridePatch.CommitmentPercentage;
        }
    }

    private static string? ResolveExecutionProfile(
        CapitalCallRequestState state,
        IReadOnlyCollection<FileAsset> attachments,
        CapitalCallProviderConfiguration providerConfiguration)
    {
        if (providerConfiguration.Profile.HasValue)
        {
            state.Profile = providerConfiguration.Profile.Value;
            state.SourceAttachmentId = null;
            state.SourceAttachmentName = null;
            return null;
        }

        var latestAttachment = attachments
            .OrderByDescending(file => file.UploadedAtUtc)
            .FirstOrDefault(file =>
            {
                var extension = Path.GetExtension(file.FileName);
                return extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".xls", StringComparison.OrdinalIgnoreCase);
            });

        if (latestAttachment is null)
        {
            state.SourceAttachmentId = null;
            state.SourceAttachmentName = null;
            state.Profile = CapitalCallExecutionProfile.AttachmentFile;
            return $"I have the fund name and capital call amount, but this tenant does not have a configured capital call data provider yet. Upload a CSV file with partner commitment data so I can continue. Expected columns include {AttachmentPromptSamples["csv"]}.";
        }

        state.Profile = CapitalCallExecutionProfile.AttachmentFile;
        state.SourceAttachmentId = latestAttachment.Id;
        state.SourceAttachmentName = latestAttachment.FileName;
        return null;
    }

    internal static decimal ResolveCommitmentPercentage(
        CapitalCallPartnerSnapshot partner,
        CapitalCallRequestState state)
    {
        var overrideValue = state.PartnerOverrides
            .FirstOrDefault(item => string.Equals(item.PartnerName, partner.PartnerName, StringComparison.OrdinalIgnoreCase))
            ?.CommitmentPercentage;

        return overrideValue ?? partner.CommitmentPercentage;
    }
}

public sealed class CapitalCallAllocationEngine : ICapitalCallAllocationEngine
{
    private readonly IReadOnlyCollection<ICapitalCallDataProvider> _dataProviders;
    private readonly IFxRateProvider _fxRateProvider;
    private readonly ILogger<CapitalCallAllocationEngine> _logger;

    public CapitalCallAllocationEngine(
        IEnumerable<ICapitalCallDataProvider> dataProviders,
        IFxRateProvider fxRateProvider,
        ILogger<CapitalCallAllocationEngine> logger)
    {
        _dataProviders = dataProviders.ToArray();
        _fxRateProvider = fxRateProvider;
        _logger = logger;
    }

    public async Task<CapitalCallComputationResult> ComputeAsync(
        string tenantId,
        string conversationId,
        string requestStateJson,
        CancellationToken cancellationToken)
    {
        var state = JsonContent.Deserialize<CapitalCallRequestState>(requestStateJson)
            ?? throw new InvalidOperationException("Capital call request state is missing.");
        _logger.LogInformation(
            "Starting capital call allocation for tenant {TenantId} conversation {ConversationId}. Profile={Profile} FundId={FundId} FundName={FundName} Amount={Amount}",
            tenantId,
            conversationId,
            state.Profile,
            state.FundId,
            state.FundName,
            state.CapitalCallAmount);

        var provider = _dataProviders.FirstOrDefault(candidate => candidate.CanHandle(state.Profile))
            ?? throw new InvalidOperationException($"No capital call data provider is registered for profile '{state.Profile}'.");
        var rootFund = await provider.GetFundAsync(
            tenantId,
            conversationId,
            state,
            state.FundId ?? throw new InvalidOperationException("Capital call state is missing the resolved fund id."),
            cancellationToken)
            ?? throw new InvalidOperationException("The root fund could not be loaded.");
        _logger.LogInformation(
            "Loaded root fund {FundId} ({FundName}) for capital call allocation in tenant {TenantId} conversation {ConversationId}. Currency={Currency}",
            rootFund.FundId,
            rootFund.FundName,
            tenantId,
            conversationId,
            rootFund.Currency);

        var traversal = await ExpandFundAsync(
            provider,
            state,
            tenantId,
            conversationId,
            rootFund,
            state.CapitalCallAmount!.Value,
            rootFund.FundName,
            [],
            0,
            cancellationToken);

        var notice = new CapitalCallNoticeDto
        {
            RootFundName = rootFund.FundName,
            RootCurrency = rootFund.Currency,
            RootCapitalCallAmount = state.CapitalCallAmount.Value,
            FundBreakdowns = traversal.FundBreakdowns,
            LeafAllocations = traversal.LeafAllocations
        };
        _logger.LogInformation(
            "Completed capital call allocation for tenant {TenantId} conversation {ConversationId}. RootFundId={FundId} FundBreakdowns={FundBreakdownCount} LeafAllocations={LeafAllocationCount}",
            tenantId,
            conversationId,
            rootFund.FundId,
            notice.FundBreakdowns.Count,
            notice.LeafAllocations.Count);

        return new CapitalCallComputationResult
        {
            Notice = notice,
            NoticeDtoJson = JsonContent.Serialize(notice),
            RootFundName = rootFund.FundName,
            RootCurrency = rootFund.Currency,
            RootCapitalCallAmount = state.CapitalCallAmount.Value
        };
    }

    private async Task<CapitalCallTraversalResult> ExpandFundAsync(
        ICapitalCallDataProvider provider,
        CapitalCallRequestState state,
        string tenantId,
        string conversationId,
        CapitalCallFundSnapshot fund,
        decimal amountToRaise,
        string path,
        IReadOnlyCollection<string> fundPath,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth >= 10)
        {
            _logger.LogWarning(
                "Capital call allocation exceeded feeder depth limit for tenant {TenantId} conversation {ConversationId}. FundId={FundId} FundName={FundName} Depth={Depth}",
                tenantId,
                conversationId,
                fund.FundId,
                fund.FundName,
                depth);
            throw new InvalidOperationException($"Feeder depth exceeded the supported limit while expanding {fund.FundName}.");
        }

        if (fundPath.Contains(fund.FundId, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Capital call allocation detected feeder cycle for tenant {TenantId} conversation {ConversationId}. FundId={FundId} FundName={FundName}",
                tenantId,
                conversationId,
                fund.FundId,
                fund.FundName);
            throw new InvalidOperationException($"Detected a feeder cycle while expanding {fund.FundName}.");
        }
        _logger.LogInformation(
            "Expanding capital call fund node for tenant {TenantId} conversation {ConversationId}. FundId={FundId} FundName={FundName} Amount={Amount} Currency={Currency} Depth={Depth} Path={Path}",
            tenantId,
            conversationId,
            fund.FundId,
            fund.FundName,
            amountToRaise,
            fund.Currency,
            depth,
            path);

        var traversal = new CapitalCallTraversalResult();
        var sortedPartners = fund.Partners
            .OrderBy(partner => partner.PartnerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var partnerAllocations = new List<CapitalCallPartnerAllocation>(sortedPartners.Length);
        var rawAllocations = sortedPartners
            .Select(partner => new
            {
                Partner = partner,
                Percentage = CapitalCallRequestPreparationService.ResolveCommitmentPercentage(partner, state),
                RawAmount = amountToRaise * (CapitalCallRequestPreparationService.ResolveCommitmentPercentage(partner, state) / 100m)
            })
            .ToArray();

        var roundedAllocations = rawAllocations
            .Select(item => new
            {
                item.Partner,
                item.Percentage,
                Amount = decimal.Round(item.RawAmount, 2, MidpointRounding.AwayFromZero)
            })
            .ToArray();

        var balance = decimal.Round(amountToRaise - roundedAllocations.Sum(item => item.Amount), 2, MidpointRounding.AwayFromZero);
        if (roundedAllocations.Length > 0 && balance != 0m)
        {
            roundedAllocations[^1] = new
            {
                roundedAllocations[^1].Partner,
                roundedAllocations[^1].Percentage,
                Amount = decimal.Round(roundedAllocations[^1].Amount + balance, 2, MidpointRounding.AwayFromZero)
            };
        }

        foreach (var allocation in roundedAllocations)
        {
            if (allocation.Partner.Kind == CapitalCallParticipantKind.FeederFund)
            {
                var childFund = await provider.GetFundAsync(
                    tenantId,
                    conversationId,
                    state,
                    allocation.Partner.ChildFundId ?? throw new InvalidOperationException("Child fund id is missing."),
                    cancellationToken)
                    ?? throw new InvalidOperationException($"The child fund for {allocation.Partner.PartnerName} could not be loaded.");

                var childAmount = await _fxRateProvider.ConvertAsync(
                    allocation.Amount,
                    fund.Currency,
                    childFund.Currency,
                    cancellationToken);
                _logger.LogInformation(
                    "Converted feeder allocation across fund boundary for tenant {TenantId} conversation {ConversationId}. ParentFundId={ParentFundId} ChildFundId={ChildFundId} PartnerName={PartnerName} ParentAmount={ParentAmount} ParentCurrency={ParentCurrency} ChildAmount={ChildAmount} ChildCurrency={ChildCurrency}",
                    tenantId,
                    conversationId,
                    fund.FundId,
                    childFund.FundId,
                    allocation.Partner.PartnerName,
                    allocation.Amount,
                    fund.Currency,
                    childAmount,
                    childFund.Currency);

                partnerAllocations.Add(new CapitalCallPartnerAllocation
                {
                    PartnerName = allocation.Partner.PartnerName,
                    PartnerType = CapitalCallParticipantKind.FeederFund.ToString(),
                    PartnerCurrency = allocation.Partner.Currency,
                    CommitmentPercentage = allocation.Percentage,
                    ContributionAmount = allocation.Amount,
                    ChildFundId = childFund.FundId,
                    ChildFundName = childFund.FundName,
                    ChildFundCurrency = childFund.Currency
                });

                var childPath = $"{path} -> {childFund.FundName}";
                var childTraversal = await ExpandFundAsync(
                    provider,
                    state,
                    tenantId,
                    conversationId,
                    childFund,
                    childAmount,
                    childPath,
                    [..fundPath, fund.FundId],
                    depth + 1,
                    cancellationToken);

                traversal.FundBreakdowns.AddRange(childTraversal.FundBreakdowns);
                traversal.LeafAllocations.AddRange(childTraversal.LeafAllocations);
                continue;
            }

            partnerAllocations.Add(new CapitalCallPartnerAllocation
            {
                PartnerName = allocation.Partner.PartnerName,
                PartnerType = CapitalCallParticipantKind.Investor.ToString(),
                PartnerCurrency = allocation.Partner.Currency,
                CommitmentPercentage = allocation.Percentage,
                ContributionAmount = allocation.Amount
            });

            traversal.LeafAllocations.Add(new CapitalCallLeafAllocation
            {
                Path = $"{path} -> {allocation.Partner.PartnerName}",
                ParentFundPath = path,
                InvestorName = allocation.Partner.PartnerName,
                Currency = allocation.Amount > 0 ? fund.Currency : allocation.Partner.Currency,
                CommitmentPercentage = allocation.Percentage,
                ContributionAmount = allocation.Amount
            });
        }

        traversal.FundBreakdowns.Insert(0, new CapitalCallFundBreakdown
        {
            FundId = fund.FundId,
            FundName = fund.FundName,
            FundCurrency = fund.Currency,
            Path = path,
            AmountToRaise = decimal.Round(amountToRaise, 2, MidpointRounding.AwayFromZero),
            PartnerAllocations = partnerAllocations
        });
        _logger.LogInformation(
            "Completed capital call fund node expansion for tenant {TenantId} conversation {ConversationId}. FundId={FundId} FundName={FundName} PartnerAllocations={PartnerAllocationCount} LeafAllocations={LeafAllocationCount}",
            tenantId,
            conversationId,
            fund.FundId,
            fund.FundName,
            partnerAllocations.Count,
            traversal.LeafAllocations.Count);

        return traversal;
    }
}

public sealed class CapitalCallResultMaterializer : ICapitalCallResultMaterializer
{
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly IAgentOperationRepository _agentOperationRepository;
    private readonly IFileStorageService _fileStorageService;
    private readonly ILogger<CapitalCallResultMaterializer> _logger;

    public CapitalCallResultMaterializer(
        IAgentOperationRepository agentOperationRepository,
        IFileStorageService fileStorageService,
        ILogger<CapitalCallResultMaterializer> logger)
    {
        _agentOperationRepository = agentOperationRepository;
        _fileStorageService = fileStorageService;
        _logger = logger;
    }

    public async Task<CapitalCallMaterializationResult> MaterializeAsync(
        string tenantId,
        string conversationId,
        string operationId,
        string requestStateJson,
        string noticeDtoJson,
        CancellationToken cancellationToken)
    {
        var state = JsonContent.Deserialize<CapitalCallRequestState>(requestStateJson)
            ?? throw new InvalidOperationException("Capital call state could not be loaded.");
        var notice = JsonContent.Deserialize<CapitalCallNoticeDto>(noticeDtoJson)
            ?? throw new InvalidOperationException("Capital call DTO could not be loaded.");
        var operation = await _agentOperationRepository.GetAsync(operationId, tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Operation '{operationId}' could not be found.");
        _logger.LogInformation(
            "Materializing capital call result for tenant {TenantId} conversation {ConversationId} operation {OperationId}. Profile={Profile} FundName={FundName}",
            tenantId,
            conversationId,
            operationId,
            state.Profile,
            notice.RootFundName);

        await using var stream = BuildWorkbookStream(notice);
        var file = await _fileStorageService.SaveAsync(
            stream,
            $"{Slugify(notice.RootFundName)}-capital-call-output.xlsx",
            ExcelContentType,
            conversationId,
            new TenantExecutionContext(tenantId, "workflow", "Capital Call Workflow"),
            cancellationToken);

        var downloadRoute = $"/api/files/{file.Id}/download";
        operation.Title = $"Capital Call Output for {notice.RootFundName}";
        operation.Status = AgentOperationStatus.Completed;
        operation.CurrentStep = "ExcelReady";
        operation.PendingClarification = null;
        operation.ActiveReviewTaskId = null;
        operation.Summary = $"Reviewed allocations for {notice.RootFundName} and generated a downloadable Excel file.";
        operation.DataJson = JsonContent.Serialize(new CapitalCallOperationData
        {
            Profile = state.Profile.ToString(),
            FundName = notice.RootFundName,
            CapitalCallAmount = notice.RootCapitalCallAmount,
            RootCurrency = notice.RootCurrency,
            FileAssetId = file.Id,
            DownloadRoute = downloadRoute,
            NoticeDtoJson = noticeDtoJson
        });
        await _agentOperationRepository.UpsertAsync(operation, cancellationToken);
        _logger.LogInformation(
            "Generated capital call output artifact for tenant {TenantId} operation {OperationId}. FileAssetId={FileAssetId} DownloadRoute={DownloadRoute}",
            tenantId,
            operationId,
            file.Id,
            downloadRoute);

        return new CapitalCallMaterializationResult
        {
            ResultKind = "Artifact",
            Summary = operation.Summary,
            FileAssetId = file.Id,
            DownloadRoute = downloadRoute
        };
    }

    private static MemoryStream BuildWorkbookStream(CapitalCallNoticeDto notice)
    {
        using var workbook = new XLWorkbook();
        var fundSheet = workbook.Worksheets.Add("Fund Rollups");
        var leafSheet = workbook.Worksheets.Add("Leaf Allocations");

        WriteFundRollups(fundSheet, notice);
        WriteLeafAllocations(leafSheet, notice);

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static void WriteFundRollups(IXLWorksheet sheet, CapitalCallNoticeDto notice)
    {
        sheet.Cell(1, 1).Value = "Path";
        sheet.Cell(1, 2).Value = "Fund Name";
        sheet.Cell(1, 3).Value = "Fund Currency";
        sheet.Cell(1, 4).Value = "Amount To Raise";
        sheet.Cell(1, 5).Value = "Partner Name";
        sheet.Cell(1, 6).Value = "Partner Type";
        sheet.Cell(1, 7).Value = "Partner Currency";
        sheet.Cell(1, 8).Value = "Commitment Percentage";
        sheet.Cell(1, 9).Value = "Contribution Amount";
        sheet.Cell(1, 10).Value = "Child Fund Name";
        sheet.Cell(1, 11).Value = "Child Fund Currency";

        var row = 2;
        foreach (var fund in notice.FundBreakdowns)
        {
            foreach (var partner in fund.PartnerAllocations)
            {
                sheet.Cell(row, 1).Value = fund.Path;
                sheet.Cell(row, 2).Value = fund.FundName;
                sheet.Cell(row, 3).Value = fund.FundCurrency;
                sheet.Cell(row, 4).Value = fund.AmountToRaise;
                sheet.Cell(row, 5).Value = partner.PartnerName;
                sheet.Cell(row, 6).Value = partner.PartnerType;
                sheet.Cell(row, 7).Value = partner.PartnerCurrency;
                sheet.Cell(row, 8).Value = partner.CommitmentPercentage;
                sheet.Cell(row, 9).Value = partner.ContributionAmount;
                sheet.Cell(row, 10).Value = partner.ChildFundName ?? string.Empty;
                sheet.Cell(row, 11).Value = partner.ChildFundCurrency ?? string.Empty;
                row++;
            }
        }
        StyleWorksheet(sheet, 11);
    }

    private static void WriteLeafAllocations(IXLWorksheet sheet, CapitalCallNoticeDto notice)
    {
        sheet.Cell(1, 1).Value = "Path";
        sheet.Cell(1, 2).Value = "Parent Fund Path";
        sheet.Cell(1, 3).Value = "Investor Name";
        sheet.Cell(1, 4).Value = "Currency";
        sheet.Cell(1, 5).Value = "Commitment Percentage";
        sheet.Cell(1, 6).Value = "Contribution Amount";

        var row = 2;
        foreach (var leaf in notice.LeafAllocations)
        {
            sheet.Cell(row, 1).Value = leaf.Path;
            sheet.Cell(row, 2).Value = leaf.ParentFundPath;
            sheet.Cell(row, 3).Value = leaf.InvestorName;
            sheet.Cell(row, 4).Value = leaf.Currency;
            sheet.Cell(row, 5).Value = leaf.CommitmentPercentage;
            sheet.Cell(row, 6).Value = leaf.ContributionAmount;
            row++;
        }
        StyleWorksheet(sheet, 6);
    }

    private static void StyleWorksheet(IXLWorksheet sheet, int columnCount)
    {
        var headerRange = sheet.Range(1, 1, 1, columnCount);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightSteelBlue;

        for (var column = 1; column <= columnCount; column++)
        {
            sheet.Column(column).AdjustToContents();
        }
    }

    private static string Slugify(string input)
    {
        var letters = input
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();

        return string.Join(string.Empty, new string(letters).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
