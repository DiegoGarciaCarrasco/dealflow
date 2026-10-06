using System.Net;
using System.Net.Http.Json;
using DealFlow.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DealFlow.Tests;

/// <summary>Spins up the real API in memory with Service Bus and Cosmos swapped for fakes.</summary>
public class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly RecordingPublisher _publisher = new();
    private readonly InMemoryRepository _repository = new();
    private readonly HttpClient _client;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddSingleton<IDealPublisher>(_publisher);
            services.AddSingleton<IDealRepository>(_repository);
        })).CreateClient();
    }

    [Fact]
    public async Task Post_valid_deal_returns_202_and_publishes()
    {
        var response = await _client.PostAsJsonAsync("/deals", new CreateDealRequest("Contoso", "NA", 120_000m, "Renewal"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var published = Assert.Single(_publisher.Published);
        Assert.Equal($"/deals/{published.Id}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Post_invalid_deal_returns_400_and_does_not_publish()
    {
        var response = await _client.PostAsJsonAsync("/deals", new CreateDealRequest("", "NA", -5m, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task Get_returns_404_until_processed_then_200()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/deals/deal-1")).StatusCode);

        _repository.Items["deal-1"] = TestData.Deal() with { Status = DealStatus.Processed, Priority = DealPriority.Medium };
        var deal = await _client.GetFromJsonAsync<Dictionary<string, object>>("/deals/deal-1");

        Assert.Equal("Processed", deal!["status"].ToString());
    }
}
