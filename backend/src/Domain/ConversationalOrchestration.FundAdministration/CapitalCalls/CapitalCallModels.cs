using System.Globalization;

namespace ConversationalOrchestration.FundAdministration.CapitalCalls;

public enum CapitalCallExecutionProfile
{
    SaaS = 1,
    AttachmentFile = 2,
    Mcp = 3
}

public enum CapitalCallParticipantKind
{
    Investor = 1,
    FeederFund = 2
}

public sealed class CapitalCallPartnerOverride
{
    public string PartnerName { get; set; } = string.Empty;
    public decimal? CommitmentPercentage { get; set; }
}

public sealed class CapitalCallIntentPatch
{
    public string? FundName { get; set; }
    public decimal? CapitalCallAmount { get; set; }
    public string? NoticeDate { get; set; }
    public List<CapitalCallPartnerOverride> PartnerOverrides { get; set; } = [];
}

public sealed class CapitalCallRequestState
{
    public CapitalCallExecutionProfile Profile { get; set; } = CapitalCallExecutionProfile.SaaS;
    public string? SourceAttachmentId { get; set; }
    public string? SourceAttachmentName { get; set; }
    public string? FundId { get; set; }
    public string? FundName { get; set; }
    public decimal? CapitalCallAmount { get; set; }
    public string? NoticeDate { get; set; }
    public List<CapitalCallPartnerOverride> PartnerOverrides { get; set; } = [];
}

public sealed class CapitalCallPreparationResult
{
    public bool IsReady { get; set; }
    public CapitalCallRequestState RequestState { get; set; } = new();
    public string RequestStateJson { get; set; } = "{}";
    public string? ClarificationPrompt { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class CapitalCallWorkflowStart
{
    public string TenantId { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string InitialUserMessage { get; init; } = string.Empty;
    public string RequestStateJson { get; init; } = "{}";
}

public sealed class CapitalCallMaterializationResult
{
    public string ResultKind { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? Route { get; set; }
    public string? FileAssetId { get; set; }
    public string? DownloadRoute { get; set; }
}

public sealed class CapitalCallOperationData
{
    public string Profile { get; set; } = CapitalCallExecutionProfile.SaaS.ToString();
    public string FundName { get; set; } = string.Empty;
    public decimal CapitalCallAmount { get; set; }
    public string RootCurrency { get; set; } = string.Empty;
    public string? RequestStateJson { get; set; }
    public string NoticeDtoJson { get; set; } = "{}";
    public string? DraftRoute { get; set; }
    public string? FileAssetId { get; set; }
    public string? DownloadRoute { get; set; }
}

public sealed class CapitalCallFundSnapshot
{
    public string FundId { get; init; } = string.Empty;
    public string FundName { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public IReadOnlyCollection<CapitalCallPartnerSnapshot> Partners { get; init; } = Array.Empty<CapitalCallPartnerSnapshot>();
}

public sealed class CapitalCallPartnerSnapshot
{
    public string PartnerId { get; init; } = string.Empty;
    public string PartnerName { get; init; } = string.Empty;
    public decimal CommitmentPercentage { get; init; }
    public string Currency { get; init; } = string.Empty;
    public CapitalCallParticipantKind Kind { get; init; }
    public string? ChildFundId { get; init; }
}

public sealed class CapitalCallNoticeDto
{
    public string RootFundName { get; set; } = string.Empty;
    public string RootCurrency { get; set; } = string.Empty;
    public decimal RootCapitalCallAmount { get; set; }
    public IReadOnlyCollection<CapitalCallFundBreakdown> FundBreakdowns { get; set; } = Array.Empty<CapitalCallFundBreakdown>();
    public IReadOnlyCollection<CapitalCallLeafAllocation> LeafAllocations { get; set; } = Array.Empty<CapitalCallLeafAllocation>();
}

public sealed class CapitalCallFundBreakdown
{
    public string FundId { get; set; } = string.Empty;
    public string FundName { get; set; } = string.Empty;
    public string FundCurrency { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public decimal AmountToRaise { get; set; }
    public IReadOnlyCollection<CapitalCallPartnerAllocation> PartnerAllocations { get; set; } = Array.Empty<CapitalCallPartnerAllocation>();
}

public sealed class CapitalCallPartnerAllocation
{
    public string PartnerName { get; set; } = string.Empty;
    public string PartnerType { get; set; } = string.Empty;
    public string PartnerCurrency { get; set; } = string.Empty;
    public decimal CommitmentPercentage { get; set; }
    public decimal ContributionAmount { get; set; }
    public string? ChildFundId { get; set; }
    public string? ChildFundName { get; set; }
    public string? ChildFundCurrency { get; set; }
}

public sealed class CapitalCallLeafAllocation
{
    public string Path { get; set; } = string.Empty;
    public string ParentFundPath { get; set; } = string.Empty;
    public string InvestorName { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal CommitmentPercentage { get; set; }
    public decimal ContributionAmount { get; set; }
}

public sealed class CapitalCallComputationResult
{
    public CapitalCallNoticeDto Notice { get; set; } = new();
    public string NoticeDtoJson { get; set; } = "{}";
    public string RootFundName { get; set; } = string.Empty;
    public string RootCurrency { get; set; } = string.Empty;
    public decimal RootCapitalCallAmount { get; set; }
}

internal sealed class CapitalCallTraversalResult
{
    public List<CapitalCallFundBreakdown> FundBreakdowns { get; } = [];
    public List<CapitalCallLeafAllocation> LeafAllocations { get; } = [];
}

public static class CapitalCallFormatting
{
    public static string FormatAmount(decimal value, string currency) =>
        string.Create(CultureInfo.InvariantCulture, $"{currency} {value:N2}");
}
