namespace DealFlow.Core;

/// <summary>
/// Deterministic fallback used when the AI flag is off or the model call fails.
/// Keeps the pipeline working even if Azure OpenAI is throttled or down.
/// </summary>
public sealed class RuleBasedEnricher : IDealEnricher
{
    public const decimal HighThresholdUsd = 250_000m;
    public const decimal MediumThresholdUsd = 50_000m;

    public string Name => "rules";

    public Task<Enrichment> EnrichAsync(Deal deal, CancellationToken ct = default)
    {
        var priority = deal.AmountUsd switch
        {
            >= HighThresholdUsd => DealPriority.High,
            >= MediumThresholdUsd => DealPriority.Medium,
            _ => DealPriority.Low,
        };

        var summary = $"{deal.AccountName} ({deal.Region}) — {deal.AmountUsd:C0} deal.";
        return Task.FromResult(new Enrichment(priority, summary));
    }
}
