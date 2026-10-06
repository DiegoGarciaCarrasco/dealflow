using DealFlow.Core;

namespace DealFlow.Tests;

public class DealProcessorTests
{
    [Fact]
    public async Task Uses_ai_enrichment_when_flag_is_on()
    {
        var repo = new InMemoryRepository();
        var ai = new FakeEnricher(_ => new Enrichment(DealPriority.High, "Urgent renewal."));

        var outcome = await TestData.Processor(repo, ai, aiEnabled: true).ProcessAsync(TestData.Deal());

        Assert.Equal(ProcessOutcome.Processed, outcome);
        var saved = repo.Items["deal-1"];
        Assert.Equal(DealStatus.Processed, saved.Status);
        Assert.Equal(DealPriority.High, saved.Priority);
        Assert.Equal("ai", saved.EnrichedBy);
        Assert.NotNull(saved.ProcessedAt);
    }

    [Fact]
    public async Task Skips_ai_when_flag_is_off()
    {
        var repo = new InMemoryRepository();
        var ai = new FakeEnricher(_ => throw new InvalidOperationException("should not be called"));

        await TestData.Processor(repo, ai, aiEnabled: false).ProcessAsync(TestData.Deal());

        Assert.Equal(0, ai.Calls);
        Assert.Equal("rules", repo.Items["deal-1"].EnrichedBy);
    }

    [Fact]
    public async Task Falls_back_to_rules_when_ai_fails()
    {
        var repo = new InMemoryRepository();
        var ai = new FakeEnricher(_ => throw new HttpRequestException("429 Too Many Requests"));

        var outcome = await TestData.Processor(repo, ai, aiEnabled: true).ProcessAsync(TestData.Deal(amount: 300_000m));

        Assert.Equal(ProcessOutcome.Processed, outcome);
        Assert.Equal("rules", repo.Items["deal-1"].EnrichedBy);
        Assert.Equal(DealPriority.High, repo.Items["deal-1"].Priority);
    }

    [Fact]
    public async Task Duplicate_delivery_is_idempotent()
    {
        var repo = new InMemoryRepository();
        var ai = new FakeEnricher(_ => new Enrichment(DealPriority.Low, "Small deal."));
        var processor = TestData.Processor(repo, ai, aiEnabled: true);

        await processor.ProcessAsync(TestData.Deal());
        var second = await processor.ProcessAsync(TestData.Deal());

        Assert.Equal(ProcessOutcome.AlreadyProcessed, second);
        Assert.Equal(1, ai.Calls);
        Assert.Equal(1, repo.Upserts);
    }
}
