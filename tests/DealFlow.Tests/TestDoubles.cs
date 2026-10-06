using DealFlow.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace DealFlow.Tests;

internal sealed class InMemoryRepository : IDealRepository
{
    public Dictionary<string, Deal> Items { get; } = [];
    public int Upserts { get; private set; }

    public Task<Deal?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(Items.GetValueOrDefault(id));

    public Task UpsertAsync(Deal deal, CancellationToken ct = default)
    {
        Items[deal.Id] = deal;
        Upserts++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeEnricher(Func<Deal, Enrichment> behavior) : IDealEnricher
{
    public int Calls { get; private set; }
    public string Name => "ai";

    public Task<Enrichment> EnrichAsync(Deal deal, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(behavior(deal));
    }
}

internal sealed class StaticFlags(bool aiEnabled) : IFeatureFlags
{
    public Task<bool> IsEnabledAsync(string flag) => Task.FromResult(aiEnabled);
}

internal sealed class RecordingPublisher : IDealPublisher
{
    public List<Deal> Published { get; } = [];

    public Task PublishAsync(Deal deal, CancellationToken ct = default)
    {
        Published.Add(deal);
        return Task.CompletedTask;
    }
}

internal static class TestData
{
    public static Deal Deal(decimal amount = 75_000m, string id = "deal-1") => new()
    {
        Id = id,
        AccountName = "Contoso",
        Region = "NA",
        AmountUsd = amount,
        Notes = "Renewal plus expansion, needs quote by Friday.",
        SubmittedAt = DateTimeOffset.Parse("2026-09-30T12:00:00Z"),
    };

    public static DealProcessor Processor(InMemoryRepository repo, IDealEnricher ai, bool aiEnabled) =>
        new(repo, ai, new RuleBasedEnricher(), new StaticFlags(aiEnabled), TimeProvider.System,
            NullLogger<DealProcessor>.Instance);
}
