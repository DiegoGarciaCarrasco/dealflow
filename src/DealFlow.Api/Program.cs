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

// Service configuration (handling for standardized HTTP problem responses)
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddSingleton(TimeProvider.System);

// Azure authenticationn (No secrets or connection strings: every Azure call authenticates with the app's managed identity)
var credential = new DefaultAzureCredential();

// Service Bus registration
builder.Services.AddSingleton(_ => new ServiceBusClient(config["ServiceBus:FullyQualifiedNamespace"], credential));
builder.Services.AddSingleton<IDealPublisher>(sp =>
    new ServiceBusDealPublisher(
        sp.GetRequiredService<ServiceBusClient>()
            .CreateSender(config["ServiceBus:QueueName"])));

// Cosmos DB registration
builder.Services.AddSingleton(_ => new CosmosClient(config["Cosmos:Endpoint"], credential, CosmosDealRepository.ClientOptions));
builder.Services.AddSingleton<IDealRepository>(sp => 
    new CosmosDealRepository(
        sp.GetRequiredService<CosmosClient>()
            .GetContainer(config["Cosmos:Database"], config["Cosmos:Container"])));

// Build the application
var app = builder.Build();
app.UseExceptionHandler();

// Health endpoint
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
});

app.MapGet("/deals/{id}", async (string id, IDealRepository repository, CancellationToken ct) =>
    await repository.GetAsync(id, ct) is { } deal ? Results.Ok(deal) : Results.NotFound());

app.Run();

// Exposes Program to WebApplicationFactory in the integration tests.
public partial class Program;