using ConversationalOrchestration.Application.Abstractions;

namespace ConversationalOrchestration.Infrastructure.Services;

public sealed class DummyCurrencyConversionService : ICurrencyConversionService
{
    private static readonly IReadOnlyDictionary<string, decimal> UnitsPerUsd =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["USD"] = 1.0m,
            ["EUR"] = 0.92m,
            ["INR"] = 83.0m,
            ["GBP"] = 0.79m,
            ["JPY"] = 155.0m,
            ["AUD"] = 1.50m,
            ["CAD"] = 1.36m,
            ["SGD"] = 1.34m
        };

    public Task<CurrencyConversionQuote> GetConversionFactorAsync(
        string fromCurrency,
        string toCurrency,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var from = Normalize(fromCurrency);
        var to = Normalize(toCurrency);

        var fromUnitsPerUsd = UnitsPerUsd.TryGetValue(from, out var fromRate) ? fromRate : 1.0m;
        var toUnitsPerUsd = UnitsPerUsd.TryGetValue(to, out var toRate) ? toRate : 1.0m;

        var baseFactor = toUnitsPerUsd / fromUnitsPerUsd;
        var factor = decimal.Round(baseFactor * GetDateAdjustment(date), 6, MidpointRounding.AwayFromZero);

        return Task.FromResult(new CurrencyConversionQuote(from, to, date, factor));
    }

    private static string Normalize(string currency) =>
        string.IsNullOrWhiteSpace(currency) ? "USD" : currency.Trim().ToUpperInvariant();

    private static decimal GetDateAdjustment(DateOnly date)
    {
        // Small deterministic wobble so "Date" is meaningful in this dummy implementation.
        var wobble = ((date.DayNumber % 30) - 15) / 10000m; // [-0.0015, +0.0014]
        return 1.0m + wobble;
    }
}

