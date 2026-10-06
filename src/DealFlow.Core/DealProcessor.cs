using Microsoft.Extensions.Logging;

namespace DealFlow.Core;

public enum ProcessOutcome { Processed, AlreadyProcessed }

/// <summary>
/// Handles one deal message: idempotency check → enrich (AI with rules fallback) → persist.
/// Pure orchestration with injected dependencies so every branch is unit-testable.
/// </summary>
public sealed class DealProcessor(
    IDealRepository repository,
    IDealEnricher aiEnricher,
    RuleBasedEnricher rulesEnricher,
    IFeatureFlags flags,
    TimeProvider clock,
    ILogger<DealProcessor> logger)
{
    public async Task<ProcessOutcome> ProcessAsync(Deal deal, CancellationToken ct = default)
    {
        // Service Bus delivers at-least-once, so the same message can arrive twice.
        // A deal that is already Processed is skipped instead of being re-enriched (and re-billed by the LLM).
        var existing = await repository.GetAsync(deal.Id, ct);
        if (existing?.Status == DealStatus.Processed)
        {
            logger.LogInformation("Deal {DealId} already processed; skipping duplicate delivery", deal.Id);
            return ProcessOutcome.AlreadyProcessed;
        }

        var (enrichment, enrichedBy) = await EnrichAsync(deal, ct);

        var processed = deal with
        {
            Status = DealStatus.Processed,
            Priority = enrichment.Priority,
            Summary = enrichment.Summary,
            EnrichedBy = enrichedBy,
            ProcessedAt = clock.GetUtcNow(),
        };

        await repository.UpsertAsync(processed, ct);
        logger.LogInformation("Deal {DealId} processed with priority {Priority} by {Enricher}",
            deal.Id, enrichment.Priority, enrichedBy);
        return ProcessOutcome.Processed;
    }

    private async Task<(Enrichment, string)> EnrichAsync(Deal deal, CancellationToken ct)
    {
        if (await flags.IsEnabledAsync(FeatureFlags.AiEnrichment))
        {
            try
            {
                return (await aiEnricher.EnrichAsync(deal, ct), aiEnricher.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Degrade gracefully rather than dead-lettering a valid deal because the model had a bad moment.
                logger.LogWarning(ex, "AI enrichment failed for deal {DealId}; falling back to rules", deal.Id);
            }
        }

        return (await rulesEnricher.EnrichAsync(deal, ct), rulesEnricher.Name);
    }
}
