using DealFlow.Core;
using DealFlow.Worker;

namespace DealFlow.Tests;

public class ValidationAndEnrichmentTests
{
    [Fact]
    public void Valid_request_has_no_errors() =>
        Assert.Empty(DealValidator.Validate(new CreateDealRequest("Contoso", "latam", 1000m, null)));

    [Fact]
    public void Invalid_request_reports_each_field()
    {
        var errors = DealValidator.Validate(new CreateDealRequest("", "MARS", 0m, new string('x', 5000)));

        Assert.Equal(
            ["AccountName", "AmountUsd", "Notes", "Region"],
            errors.Keys.Order().ToArray());
    }

    [Theory]
    [InlineData(10_000, DealPriority.Low)]
    [InlineData(50_000, DealPriority.Medium)]
    [InlineData(250_000, DealPriority.High)]
    public async Task Rules_prioritize_by_amount(decimal amount, DealPriority expected)
    {
        var result = await new RuleBasedEnricher().EnrichAsync(TestData.Deal(amount));
        Assert.Equal(expected, result.Priority);
    }

    [Fact]
    public void Parses_well_formed_model_output()
    {
        var result = AzureOpenAiDealEnricher.Parse("""{"priority":"high","summary":"Expansion with a Friday deadline."}""");

        Assert.Equal(DealPriority.High, result.Priority);
        Assert.Equal("Expansion with a Friday deadline.", result.Summary);
    }

    [Theory]
    [InlineData("""{"priority":"urgent","summary":"x"}""")]
    [InlineData("""{"priority":"low","summary":"  "}""")]
    [InlineData("not json")]
    public void Rejects_malformed_model_output(string json) =>
        Assert.ThrowsAny<Exception>(() => AzureOpenAiDealEnricher.Parse(json));
}
