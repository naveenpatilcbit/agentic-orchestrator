namespace ConversationalOrchestration.Infrastructure.AI.Tools;

internal sealed record CurrencyConversionToolRequest(
    string FromCurrency,
    string ToCurrency,
    DateOnly Date);

internal sealed record CurrencyConversionToolResponse(
    string FromCurrency,
    string ToCurrency,
    DateOnly Date,
    decimal ConversionFactor);

