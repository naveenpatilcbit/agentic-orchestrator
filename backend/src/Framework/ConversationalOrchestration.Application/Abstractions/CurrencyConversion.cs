namespace ConversationalOrchestration.Application.Abstractions;

public interface ICurrencyConversionService
{
    Task<CurrencyConversionQuote> GetConversionFactorAsync(
        string fromCurrency,
        string toCurrency,
        DateOnly date,
        CancellationToken cancellationToken);
}

public sealed record CurrencyConversionQuote(
    string FromCurrency,
    string ToCurrency,
    DateOnly Date,
    decimal ConversionFactor);

