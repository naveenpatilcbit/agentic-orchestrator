using ConversationalOrchestration.FundAdministration.CapitalCalls;
using Microsoft.Extensions.Logging;

namespace ConversationalOrchestration.FundAdministration.CapitalCalls.Demo;

// Demo-only implementations used to make the sample runnable without external systems.
public sealed class InMemoryCapitalCallProviderConfigurationService : ICapitalCallProviderConfigurationService
{
    private static readonly IReadOnlyDictionary<string, CapitalCallProviderConfiguration> ConfiguredProviders =
        new Dictionary<string, CapitalCallProviderConfiguration>(StringComparer.OrdinalIgnoreCase)
        {
            ["tenant-demo"] = new()
            {
                Profile = CapitalCallExecutionProfile.SaaS,
                ProviderName = "InMemorySaaS"
            }
        };

    public Task<CapitalCallProviderConfiguration> ResolveAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        if (ConfiguredProviders.TryGetValue(tenantId, out var configuration))
        {
            return Task.FromResult(configuration);
        }

        return Task.FromResult(new CapitalCallProviderConfiguration());
    }
}

// Demo-only SaaS provider backed by hardcoded sample fund data.
public sealed class InMemorySaaSCapitalCallDataProvider : ICapitalCallDataProvider
{
    private readonly IReadOnlyDictionary<string, CapitalCallFundSnapshot> _fundsById;

    public InMemorySaaSCapitalCallDataProvider()
    {
        var funds = new[]
        {
            new CapitalCallFundSnapshot
            {
                FundId = "fund-apex-i",
                FundName = "Apex Fund I",
                Currency = "INR",
                Partners =
                [
                    new CapitalCallPartnerSnapshot
                    {
                        PartnerId = "partner-bluestone",
                        PartnerName = "BlueStone Capital LP",
                        CommitmentPercentage = 40m,
                        Currency = "INR",
                        Kind = CapitalCallParticipantKind.Investor
                    },
                    new CapitalCallPartnerSnapshot
                    {
                        PartnerId = "partner-northstar-feeder",
                        PartnerName = "North Star Feeder",
                        CommitmentPercentage = 60m,
                        Currency = "USD",
                        Kind = CapitalCallParticipantKind.FeederFund,
                        ChildFundId = "fund-northstar-feeder"
                    }
                ]
            },
            new CapitalCallFundSnapshot
            {
                FundId = "fund-northstar-feeder",
                FundName = "North Star Feeder",
                Currency = "USD",
                Partners =
                [
                    new CapitalCallPartnerSnapshot
                    {
                        PartnerId = "partner-orchard",
                        PartnerName = "Orchard Family Office",
                        CommitmentPercentage = 70m,
                        Currency = "USD",
                        Kind = CapitalCallParticipantKind.Investor
                    },
                    new CapitalCallPartnerSnapshot
                    {
                        PartnerId = "partner-horizon-feeder",
                        PartnerName = "Horizon Feeder II",
                        CommitmentPercentage = 30m,
                        Currency = "EUR",
                        Kind = CapitalCallParticipantKind.FeederFund,
                        ChildFundId = "fund-horizon-feeder"
                    }
                ]
            },
            new CapitalCallFundSnapshot
            {
                FundId = "fund-horizon-feeder",
                FundName = "Horizon Feeder II",
                Currency = "EUR",
                Partners =
                [
                    new CapitalCallPartnerSnapshot
                    {
                        PartnerId = "partner-olive-tree",
                        PartnerName = "Olive Tree Holdings",
                        CommitmentPercentage = 55m,
                        Currency = "EUR",
                        Kind = CapitalCallParticipantKind.Investor
                    },
                    new CapitalCallPartnerSnapshot
                    {
                        PartnerId = "partner-redwood",
                        PartnerName = "Redwood Pension Trust",
                        CommitmentPercentage = 45m,
                        Currency = "EUR",
                        Kind = CapitalCallParticipantKind.Investor
                    }
                ]
            },
            new CapitalCallFundSnapshot
            {
                FundId = "fund-summit-ii",
                FundName = "Summit Growth Fund II",
                Currency = "USD",
                Partners =
                [
                    new CapitalCallPartnerSnapshot
                    {
                        PartnerId = "partner-greenwood",
                        PartnerName = "Greenwood Endowment",
                        CommitmentPercentage = 52m,
                        Currency = "USD",
                        Kind = CapitalCallParticipantKind.Investor
                    },
                    new CapitalCallPartnerSnapshot
                    {
                        PartnerId = "partner-pinecrest",
                        PartnerName = "Pinecrest Family Capital",
                        CommitmentPercentage = 48m,
                        Currency = "USD",
                        Kind = CapitalCallParticipantKind.Investor
                    }
                ]
            }
        };

        _fundsById = funds.ToDictionary(fund => fund.FundId, StringComparer.OrdinalIgnoreCase);
    }

    public bool CanHandle(CapitalCallExecutionProfile profile) =>
        profile == CapitalCallExecutionProfile.SaaS;

    public Task<CapitalCallFundSnapshot?> ResolveRootFundAsync(
        string tenantId,
        string conversationId,
        CapitalCallRequestState state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.FundName))
        {
            return Task.FromResult<CapitalCallFundSnapshot?>(null);
        }

        var match = _fundsById.Values.FirstOrDefault(fund =>
            string.Equals(fund.FundName, state.FundName, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(match);
    }

    public Task<CapitalCallFundSnapshot?> GetFundAsync(
        string tenantId,
        string conversationId,
        CapitalCallRequestState state,
        string fundId,
        CancellationToken cancellationToken) =>
        Task.FromResult(_fundsById.TryGetValue(fundId, out var fund) ? fund : null);
}

// Demo-only FX provider backed by a small static rate table.
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

    private readonly ILogger<SampleFxRateProvider> _logger;

    public SampleFxRateProvider(ILogger<SampleFxRateProvider> logger)
    {
        _logger = logger;
    }

    public Task<decimal> ConvertAsync(
        decimal amount,
        string fromCurrency,
        string toCurrency,
        CancellationToken cancellationToken)
    {
        if (string.Equals(fromCurrency, toCurrency, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Skipping FX conversion because currencies already match. Currency={Currency} Amount={Amount}",
                fromCurrency,
                amount);
            return Task.FromResult(decimal.Round(amount, 2, MidpointRounding.AwayFromZero));
        }

        if (Rates.TryGetValue((fromCurrency.ToUpperInvariant(), toCurrency.ToUpperInvariant()), out var rate))
        {
            var converted = decimal.Round(amount * rate, 2, MidpointRounding.AwayFromZero);
            _logger.LogInformation(
                "Converted amount using configured FX rate. FromCurrency={FromCurrency} ToCurrency={ToCurrency} Rate={Rate} SourceAmount={SourceAmount} ConvertedAmount={ConvertedAmount}",
                fromCurrency,
                toCurrency,
                rate,
                amount,
                converted);
            return Task.FromResult(converted);
        }

        _logger.LogError(
            "No FX rate configured for conversion. FromCurrency={FromCurrency} ToCurrency={ToCurrency} Amount={Amount}",
            fromCurrency,
            toCurrency,
            amount);
        throw new InvalidOperationException($"No FX rate is configured for {fromCurrency} -> {toCurrency}.");
    }
}
