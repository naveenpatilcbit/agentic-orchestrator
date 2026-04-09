using System.Globalization;
using System.Text;
using ConversationalOrchestration.Application.Abstractions;

namespace ConversationalOrchestration.FundAdministration.CapitalCalls;

public sealed class CsvAttachmentCapitalCallDataProvider : ICapitalCallDataProvider
{
    private readonly IFileAssetRepository _fileAssetRepository;

    public CsvAttachmentCapitalCallDataProvider(IFileAssetRepository fileAssetRepository)
    {
        _fileAssetRepository = fileAssetRepository;
    }

    public bool CanHandle(CapitalCallExecutionProfile profile) =>
        profile == CapitalCallExecutionProfile.AttachmentFile;

    public async Task<CapitalCallFundSnapshot?> ResolveRootFundAsync(
        string tenantId,
        string conversationId,
        CapitalCallRequestState state,
        CancellationToken cancellationToken)
    {
        var funds = await LoadFundsAsync(tenantId, state, cancellationToken);
        if (funds.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(state.FundName) && funds.Count == 1)
        {
            return funds.Values.Single();
        }

        return funds.Values.FirstOrDefault(fund =>
            string.Equals(fund.FundName, state.FundName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<CapitalCallFundSnapshot?> GetFundAsync(
        string tenantId,
        string conversationId,
        CapitalCallRequestState state,
        string fundId,
        CancellationToken cancellationToken)
    {
        var funds = await LoadFundsAsync(tenantId, state, cancellationToken);
        return funds.TryGetValue(fundId, out var fund) ? fund : null;
    }

    private async Task<IReadOnlyDictionary<string, CapitalCallFundSnapshot>> LoadFundsAsync(
        string tenantId,
        CapitalCallRequestState state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.SourceAttachmentId))
        {
            return new Dictionary<string, CapitalCallFundSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        var file = await _fileAssetRepository.GetAsync(state.SourceAttachmentId, tenantId, cancellationToken);
        if (file is null || !File.Exists(file.RelativePath))
        {
            return new Dictionary<string, CapitalCallFundSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        var lines = await File.ReadAllLinesAsync(file.RelativePath, cancellationToken);
        if (lines.Length <= 1)
        {
            return new Dictionary<string, CapitalCallFundSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        var headers = SplitCsv(lines[0]).Select(NormalizeHeader).ToArray();
        var rows = lines.Skip(1)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(SplitCsv)
            .ToArray();

        var fundRows = new Dictionary<string, List<(string[] Values, IReadOnlyDictionary<string, int> Map)>>(StringComparer.OrdinalIgnoreCase);
        var headerMap = headers
            .Select((header, index) => new { header, index })
            .ToDictionary(item => item.header, item => item.index, StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var fundName = GetValue(row, headerMap, "fundname");
            if (string.IsNullOrWhiteSpace(fundName))
            {
                continue;
            }

            if (!fundRows.TryGetValue(fundName, out var bucket))
            {
                bucket = [];
                fundRows[fundName] = bucket;
            }

            bucket.Add((row, headerMap));
        }

        return fundRows.Values
            .Select(BuildFundSnapshot)
            .ToDictionary(fund => fund.FundId, StringComparer.OrdinalIgnoreCase);
    }

    private static CapitalCallFundSnapshot BuildFundSnapshot(
        List<(string[] Values, IReadOnlyDictionary<string, int> Map)> rows)
    {
        var first = rows[0];
        var fundName = GetValue(first.Values, first.Map, "fundname");
        var fundCurrency = GetValue(first.Values, first.Map, "fundcurrency");

        return new CapitalCallFundSnapshot
        {
            FundId = Slugify(fundName),
            FundName = fundName,
            Currency = string.IsNullOrWhiteSpace(fundCurrency) ? "USD" : fundCurrency.ToUpperInvariant(),
            Partners = rows.Select(row =>
            {
                var partnerType = GetValue(row.Values, row.Map, "partnertype");
                var childFundName = GetValue(row.Values, row.Map, "childfundname");
                return new CapitalCallPartnerSnapshot
                {
                    PartnerId = Slugify(GetValue(row.Values, row.Map, "partnername")),
                    PartnerName = GetValue(row.Values, row.Map, "partnername"),
                    CommitmentPercentage = decimal.TryParse(
                        GetValue(row.Values, row.Map, "commitmentpercentage"),
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out var percentage)
                        ? percentage
                        : 0m,
                    Currency = GetValue(row.Values, row.Map, "partnercurrency").ToUpperInvariant(),
                    Kind = string.Equals(partnerType, "feeder", StringComparison.OrdinalIgnoreCase)
                        ? CapitalCallParticipantKind.FeederFund
                        : CapitalCallParticipantKind.Investor,
                    ChildFundId = string.IsNullOrWhiteSpace(childFundName) ? null : Slugify(childFundName)
                };
            }).ToArray()
        };
    }

    private static string[] SplitCsv(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var insideQuotes = false;

        foreach (var character in line)
        {
            switch (character)
            {
                case '"' when insideQuotes:
                    insideQuotes = false;
                    break;
                case '"' when !insideQuotes:
                    insideQuotes = true;
                    break;
                case ',' when !insideQuotes:
                    values.Add(current.ToString().Trim());
                    current.Clear();
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        values.Add(current.ToString().Trim());
        return values.ToArray();
    }

    private static string NormalizeHeader(string header) =>
        new string(header.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string GetValue(string[] row, IReadOnlyDictionary<string, int> map, string normalizedHeader)
    {
        if (!map.TryGetValue(normalizedHeader, out var index) || index >= row.Length)
        {
            return string.Empty;
        }

        return row[index].Trim();
    }

    private static string Slugify(string value) =>
        string.Join(
            string.Empty,
            value
                .ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : '-')
                .ToArray())
            .Replace("--", "-", StringComparison.Ordinal);
}
