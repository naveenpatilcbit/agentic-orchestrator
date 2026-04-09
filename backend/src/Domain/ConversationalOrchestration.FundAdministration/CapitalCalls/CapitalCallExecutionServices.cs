using System.Globalization;
using System.Text;
using ConversationalOrchestration.Application.Abstractions;
using ConversationalOrchestration.Application.Support;
using ConversationalOrchestration.Domain.Files;
using ConversationalOrchestration.Domain.Operations;

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

public sealed class CapitalCallRequestPreparationService : ICapitalCallRequestPreparationService
{
    private static readonly IReadOnlyDictionary<string, string> AttachmentPromptSamples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["csv"] = "FundName,FundCurrency,PartnerName,CommitmentPercentage,PartnerCurrency,PartnerType,ChildFundName"
    };

    private readonly IFileAssetRepository _fileAssetRepository;
    private readonly IReadOnlyCollection<ICapitalCallDataProvider> _dataProviders;

    public CapitalCallRequestPreparationService(
        IFileAssetRepository fileAssetRepository,
        IEnumerable<ICapitalCallDataProvider> dataProviders)
    {
        _fileAssetRepository = fileAssetRepository;
        _dataProviders = dataProviders.ToArray();
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
        ResolveExecutionProfile(state, attachments);

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
            return new CapitalCallPreparationResult
            {
                IsReady = false,
                RequestState = state,
                RequestStateJson = JsonContent.Serialize(state),
                ClarificationPrompt = $"I still need the {string.Join(" and ", missing)} before I can calculate the capital call allocations.",
                Summary = "Waiting for the remaining capital call inputs."
            };
        }

        if (state.Profile == CapitalCallExecutionProfile.AttachmentFile &&
            !string.IsNullOrWhiteSpace(state.SourceAttachmentName) &&
            !Path.GetExtension(state.SourceAttachmentName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
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
            return new CapitalCallPreparationResult
            {
                IsReady = false,
                RequestState = state,
                RequestStateJson = JsonContent.Serialize(state),
                ClarificationPrompt = $"I couldn't find a fund named '{state.FundName}'. Please confirm the fund name or upload a CSV source for this notice.",
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
            return new CapitalCallPreparationResult
            {
                IsReady = false,
                RequestState = state,
                RequestStateJson = JsonContent.Serialize(state),
                ClarificationPrompt = validationIssue,
                Summary = "Waiting for clarification before the allocation engine can continue."
            };
        }

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
            return $"The feeder structure under {fund.FundName} is deeper than the supported limit of 10 levels. Please review the fund hierarchy.";
        }

        if (path.Contains(fund.FundId, StringComparer.OrdinalIgnoreCase))
        {
            return $"I detected a feeder cycle while expanding {fund.FundName}. Please review the fund hierarchy before retrying.";
        }

        if (fund.Partners.Count == 0)
        {
            return $"The fund {fund.FundName} does not have any partner commitment data yet.";
        }

        var totalPercentage = fund.Partners.Sum(partner => ResolveCommitmentPercentage(partner, state));
        if (decimal.Round(totalPercentage, 2, MidpointRounding.AwayFromZero) != 100m)
        {
            return $"The partner commitment percentages for {fund.FundName} add up to {totalPercentage:N2}%. Please correct them before I continue.";
        }

        foreach (var feeder in fund.Partners.Where(partner => partner.Kind == CapitalCallParticipantKind.FeederFund))
        {
            if (string.IsNullOrWhiteSpace(feeder.ChildFundId))
            {
                return $"The feeder partner {feeder.PartnerName} in {fund.FundName} does not point to a child fund.";
            }

            var childFund = await provider.GetFundAsync(tenantId, conversationId, state, feeder.ChildFundId, cancellationToken);
            if (childFund is null)
            {
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

    private static void ResolveExecutionProfile(
        CapitalCallRequestState state,
        IReadOnlyCollection<FileAsset> attachments)
    {
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
            state.Profile = CapitalCallExecutionProfile.SaaS;
            state.SourceAttachmentId = null;
            state.SourceAttachmentName = null;
            return;
        }

        state.Profile = CapitalCallExecutionProfile.AttachmentFile;
        state.SourceAttachmentId = latestAttachment.Id;
        state.SourceAttachmentName = latestAttachment.FileName;
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

    public CapitalCallAllocationEngine(
        IEnumerable<ICapitalCallDataProvider> dataProviders,
        IFxRateProvider fxRateProvider)
    {
        _dataProviders = dataProviders.ToArray();
        _fxRateProvider = fxRateProvider;
    }

    public async Task<CapitalCallComputationResult> ComputeAsync(
        string tenantId,
        string conversationId,
        string requestStateJson,
        CancellationToken cancellationToken)
    {
        var state = JsonContent.Deserialize<CapitalCallRequestState>(requestStateJson)
            ?? throw new InvalidOperationException("Capital call request state is missing.");

        var provider = _dataProviders.FirstOrDefault(candidate => candidate.CanHandle(state.Profile))
            ?? throw new InvalidOperationException($"No capital call data provider is registered for profile '{state.Profile}'.");
        var rootFund = await provider.GetFundAsync(
            tenantId,
            conversationId,
            state,
            state.FundId ?? throw new InvalidOperationException("Capital call state is missing the resolved fund id."),
            cancellationToken)
            ?? throw new InvalidOperationException("The root fund could not be loaded.");

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
            throw new InvalidOperationException($"Feeder depth exceeded the supported limit while expanding {fund.FundName}.");
        }

        if (fundPath.Contains(fund.FundId, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Detected a feeder cycle while expanding {fund.FundName}.");
        }

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

        return traversal;
    }
}

public sealed class CapitalCallResultMaterializer : ICapitalCallResultMaterializer
{
    private readonly IAgentOperationRepository _agentOperationRepository;
    private readonly IFileStorageService _fileStorageService;

    public CapitalCallResultMaterializer(
        IAgentOperationRepository agentOperationRepository,
        IFileStorageService fileStorageService)
    {
        _agentOperationRepository = agentOperationRepository;
        _fileStorageService = fileStorageService;
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

        if (state.Profile == CapitalCallExecutionProfile.SaaS)
        {
            var draftRoute = $"/funds/{Slugify(notice.RootFundName)}/capital-call-drafts/{operationId}";
            operation.Title = $"Capital Call Notice for {notice.RootFundName}";
            operation.Status = AgentOperationStatus.Completed;
            operation.CurrentStep = "DraftReady";
            operation.PendingClarification = null;
            operation.Summary = $"Calculated allocations and created a draft notice for {notice.RootFundName}.";
            operation.DataJson = JsonContent.Serialize(new CapitalCallOperationData
            {
                Profile = state.Profile.ToString(),
                FundName = notice.RootFundName,
                CapitalCallAmount = notice.RootCapitalCallAmount,
                RootCurrency = notice.RootCurrency,
                DraftRoute = draftRoute,
                NoticeDtoJson = noticeDtoJson
            });
            await _agentOperationRepository.UpsertAsync(operation, cancellationToken);

            return new CapitalCallMaterializationResult
            {
                ResultKind = "DraftNotice",
                Summary = operation.Summary,
                Route = draftRoute
            };
        }

        var csv = BuildCsv(notice);
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var file = await _fileStorageService.SaveAsync(
            stream,
            $"{Slugify(notice.RootFundName)}-capital-call-output.csv",
            "text/csv",
            conversationId,
            new TenantExecutionContext(tenantId, "workflow", "Capital Call Workflow"),
            cancellationToken);

        var downloadRoute = $"/api/files/{file.Id}/download";
        operation.Title = $"Capital Call Output for {notice.RootFundName}";
        operation.Status = AgentOperationStatus.Completed;
        operation.CurrentStep = "ArtifactReady";
        operation.PendingClarification = null;
        operation.Summary = $"Calculated allocations for {notice.RootFundName} and generated a downloadable output file.";
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

        return new CapitalCallMaterializationResult
        {
            ResultKind = "Artifact",
            Summary = operation.Summary,
            FileAssetId = file.Id,
            DownloadRoute = downloadRoute
        };
    }

    private static string BuildCsv(CapitalCallNoticeDto notice)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Section,Path,FundName,FundCurrency,AmountToRaise,PartnerName,PartnerType,PartnerCurrency,CommitmentPercentage,ContributionAmount,ChildFundName,ChildFundCurrency");

        foreach (var fund in notice.FundBreakdowns)
        {
            foreach (var partner in fund.PartnerAllocations)
            {
                builder.AppendLine(string.Join(",",
                    Escape("FundRollup"),
                    Escape(fund.Path),
                    Escape(fund.FundName),
                    Escape(fund.FundCurrency),
                    Escape(fund.AmountToRaise.ToString("0.00", CultureInfo.InvariantCulture)),
                    Escape(partner.PartnerName),
                    Escape(partner.PartnerType),
                    Escape(partner.PartnerCurrency),
                    Escape(partner.CommitmentPercentage.ToString("0.00", CultureInfo.InvariantCulture)),
                    Escape(partner.ContributionAmount.ToString("0.00", CultureInfo.InvariantCulture)),
                    Escape(partner.ChildFundName),
                    Escape(partner.ChildFundCurrency)));
            }
        }

        foreach (var leaf in notice.LeafAllocations)
        {
            builder.AppendLine(string.Join(",",
                Escape("LeafAllocation"),
                Escape(leaf.Path),
                Escape(string.Empty),
                Escape(leaf.Currency),
                Escape(string.Empty),
                Escape(leaf.InvestorName),
                Escape("Investor"),
                Escape(leaf.Currency),
                Escape(leaf.CommitmentPercentage.ToString("0.00", CultureInfo.InvariantCulture)),
                Escape(leaf.ContributionAmount.ToString("0.00", CultureInfo.InvariantCulture)),
                Escape(string.Empty),
                Escape(string.Empty)));
        }

        return builder.ToString();
    }

    private static string Escape(string? value)
    {
        var normalized = value ?? string.Empty;
        return $"\"{normalized.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
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

public sealed class SampleFxRateProvider : IFxRateProvider
{
    private static readonly IReadOnlyDictionary<(string From, string To), decimal> Rates = new Dictionary<(string From, string To), decimal>
    {
        [("INR", "USD")] = 0.0120m,
        [("USD", "INR")] = 83.0000m,
        [("USD", "EUR")] = 0.9200m,
        [("EUR", "USD")] = 1.0870m,
        [("INR", "EUR")] = 0.0111m,
        [("EUR", "INR")] = 89.8000m
    };

    public Task<decimal> ConvertAsync(
        decimal amount,
        string fromCurrency,
        string toCurrency,
        CancellationToken cancellationToken)
    {
        if (string.Equals(fromCurrency, toCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(decimal.Round(amount, 2, MidpointRounding.AwayFromZero));
        }

        if (Rates.TryGetValue((fromCurrency.ToUpperInvariant(), toCurrency.ToUpperInvariant()), out var rate))
        {
            return Task.FromResult(decimal.Round(amount * rate, 2, MidpointRounding.AwayFromZero));
        }

        throw new InvalidOperationException($"No FX rate is configured for {fromCurrency} -> {toCurrency}.");
    }
}
