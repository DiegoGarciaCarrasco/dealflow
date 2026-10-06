namespace DealFlow.Core;

/// <summary>Publishes a newly accepted deal for asynchronous processing (Service Bus in production).</summary>
public interface IDealPublisher
{
    Task PublishAsync(Deal deal, CancellationToken ct = default);
}

public interface IDealRepository
{
    Task<Deal?> GetAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(Deal deal, CancellationToken ct = default);
}

public sealed record Enrichment(DealPriority Priority, string Summary);

/// <summary>Scores and summarizes a deal. Implemented by an LLM (Azure OpenAI) and by a deterministic rules fallback.</summary>
public interface IDealEnricher
{
    string Name { get; }
    Task<Enrichment> EnrichAsync(Deal deal, CancellationToken ct = default);
}

/// <summary>Thin wrapper so the processor doesn't depend on a feature-management library directly.</summary>
public interface IFeatureFlags
{
    Task<bool> IsEnabledAsync(string flag);
}

public static class FeatureFlags
{
    /// <summary>Turns LLM enrichment on/off at runtime without a redeploy (see docs/adr/0003).</summary>
    public const string AiEnrichment = "AiEnrichment";
}