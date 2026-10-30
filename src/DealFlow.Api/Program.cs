using System.Threading.RateLimiting;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using DealFlow.Api;
using DealFlow.Core;
using Microsoft.Azure.Cosmos;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Telemetry: requests, dependencies (Service Bus, Cosmos), and logs flow to Application Insights.
if (!string.IsNullOrEmpty(config["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor();

builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);

// The demo UI is public and every accepted deal costs an Azure OpenAI call, so cap submissions per client IP.
// Over the limit, the API answers 429 Too Many Requests instead of queuing more work.
var dealsPerMinute = config.GetValue("RateLimit:DealsPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("submit", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = dealsPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

// No secrets or connection strings: every Azure call authenticates with the app's managed identity.
var credential = new DefaultAzureCredential();

builder.Services.AddSingleton(_ => new ServiceBusClient(config["ServiceBus:FullyQualifiedNamespace"], credential));
builder.Services.AddSingleton<IDealPublisher>(sp =>
    new ServiceBusDealPublisher(sp.GetRequiredService<ServiceBusClient>().CreateSender(config["ServiceBus:QueueName"])));

builder.Services.AddSingleton(_ => new CosmosClient(config["Cosmos:Endpoint"], credential, CosmosDealRepository.ClientOptions));
builder.Services.AddSingleton<IDealRepository>(sp => new CosmosDealRepository(
    sp.GetRequiredService<CosmosClient>().GetContainer(config["Cosmos:Database"], config["Cosmos:Container"])));

var app = builder.Build();
app.UseExceptionHandler();

// Serves the single-page UI from wwwroot/index.html at "/". Same origin as the API, so no CORS setup.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Async request-reply: accept fast, process in the background, let the client poll the Location.
app.MapPost("/deals", async (CreateDealRequest? request, IDealPublisher publisher, TimeProvider clock, CancellationToken ct) =>
{
    var errors = DealValidator.Validate(request);
    if (errors.Count > 0)
        return Results.ValidationProblem(errors);

    var deal = Deal.FromRequest(request!, clock);
    await publisher.PublishAsync(deal, ct);
    return Results.Accepted($"/deals/{deal.Id}", new { deal.Id, Status = DealStatus.Received.ToString() });
}).RequireRateLimiting("submit");

app.MapGet("/deals/{id}", async (string id, IDealRepository repository, CancellationToken ct) =>
    await repository.GetAsync(id, ct) is { } deal ? Results.Ok(deal) : Results.NotFound());

// Recent processed deals for the UI's history list. Only deals the worker has saved appear here.
app.MapGet("/deals", async (int? limit, IDealRepository repository, CancellationToken ct) =>
    Results.Ok(await repository.ListRecentAsync(Math.Clamp(limit ?? 20, 1, 50), ct)));

app.Run();

// Exposes Program to WebApplicationFactory in the integration tests.
public partial class Program;
