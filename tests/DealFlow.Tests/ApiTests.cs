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
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = CreateClient();
    }

    private HttpClient CreateClient(int dealsPerMinute = 10) =>
        _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimit:DealsPerMinute", dealsPerMinute.ToString());
            b.ConfigureServices(services =>
            {
                services.AddSingleton<IDealPublisher>(_publisher);
                services.AddSingleton<IDealRepository>(_repository);
            });
        }).CreateClient();

    [Fact]
    public async Task Root_serves_the_web_ui()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("DealFlow", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task List_returns_newest_first_and_respects_limit()
    {
        var t0 = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        for (var i = 1; i <= 3; i++)
            _repository.Items[$"d{i}"] = TestData.Deal(id: $"d{i}") with { SubmittedAt = t0.AddMinutes(i) };

        var deals = await _client.GetFromJsonAsync<List<Dictionary<string, object>>>("/deals?limit=2");

        Assert.Equal(["d3", "d2"], deals!.Select(d => d["id"].ToString()).ToArray());
    }

    [Fact]
    public async Task Post_over_rate_limit_returns_429_and_does_not_publish()
    {
        var client = CreateClient(dealsPerMinute: 2);
        var request = new CreateDealRequest("Contoso", "NA", 1_000m, null);

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/deals", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/deals", request)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/deals", request)).StatusCode);
        Assert.Equal(2, _publisher.Published.Count);
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
