using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    [JsonConverter(typeof(SingleOrArrayJsonConverter<CapitalCallPartnerOverride>))]
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
    [JsonConverter(typeof(SingleOrArrayJsonConverter<CapitalCallPartnerOverride>))]
    public List<CapitalCallPartnerOverride> PartnerOverrides { get; set; } = [];
}

public sealed class CapitalCallPreparationResult
{
    public bool IsReady { get; set; }
    public CapitalCallRequestState RequestState { get; set; } = new();
    public string? ClarificationPrompt { get; set; }
    public string Summary { get; set; } = string.Empty;
}

public sealed class CapitalCallProviderConfiguration
{
    public CapitalCallExecutionProfile? Profile { get; init; }
    public string? ProviderName { get; init; }
}

public sealed class CapitalCallWorkflowStart
{
    public string TenantId { get; init; } = string.Empty;
    public string ConversationId { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public CapitalCallRequestState RequestState { get; init; } = new();
}

public sealed class CapitalCallReviewArtifactResult
{
    public string DownloadRoute { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
}

public sealed class TemplateOutputGenerationResult
{
    public string FileAssetId { get; set; } = string.Empty;
    public string DownloadRoute { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string SourceOperationId { get; set; } = string.Empty;
}

public sealed class CapitalCallOperationData
{
    public string? OutputId { get; set; }
    public string FundName { get; set; } = string.Empty;
    public CapitalCallRequestState? RequestState { get; set; }
    public CapitalCallExtractionReviewPayload? ReviewedExtraction { get; set; }
    public CapitalCallNoticeDto? Notice { get; set; }
    public string? ReviewDownloadRoute { get; set; }
    public string? ReviewFileName { get; set; }
}

public sealed class CapitalCallExtractionReviewPayload
{
    public string TenantId { get; set; } = string.Empty;
    public string ConversationId { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public CapitalCallRequestState RequestState { get; set; } = new();
    public CapitalCallReviewFundNode RootFund { get; set; } = new();
    public string? ReviewDownloadRoute { get; set; }
    public string? ReviewFileName { get; set; }
}

public sealed class CapitalCallReviewFundNode
{
    public string FundId { get; set; } = string.Empty;
    public string FundName { get; set; } = string.Empty;
    public string FundCurrency { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public List<CapitalCallReviewPartnerNode> Partners { get; set; } = [];
}

public sealed class CapitalCallReviewPartnerNode
{
    public string PartnerId { get; set; } = string.Empty;
    public string PartnerName { get; set; } = string.Empty;
    public string PartnerType { get; set; } = string.Empty;
    public string PartnerCurrency { get; set; } = string.Empty;
    public decimal CommitmentPercentage { get; set; }
    public CapitalCallReviewFundNode? ChildFund { get; set; }
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

internal sealed class SingleOrArrayJsonConverter<TItem> : JsonConverter<List<TItem>>
{
    public override List<TItem> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            return JsonSerializer.Deserialize<List<TItem>>(ref reader, options) ?? [];
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var item = JsonSerializer.Deserialize<TItem>(ref reader, options);
            return item is null ? [] : [item];
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            using var document = JsonDocument.Parse(raw);
            var nestedJson = Encoding.UTF8.GetBytes(document.RootElement.GetRawText());
            var nestedReader = new Utf8JsonReader(nestedJson);
            nestedReader.Read();
            return Read(ref nestedReader, typeToConvert, options);
        }

        throw new JsonException($"Unsupported JSON token '{reader.TokenType}' for {typeof(TItem).Name} collection.");
    }

    public override void Write(Utf8JsonWriter writer, List<TItem> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
