using Azure.AI.OpenAI;
using Azure.Identity;
using DealFlow.Core;
using DealFlow.Worker;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.FeatureManagement;

var credential = new DefaultAzureCredential();

var builder = FunctionsApplication.CreateBuilder(args);
var config = builder.Configuration;

// Feature flags live in Azure App Configuration so AI enrichment can be flipped (or rolled out to a
// percentage of traffic) without redeploying. Locally, flags fall back to local.settings.json.
var appConfigEndpoint = config["AppConfig:Endpoint"];
if (!string.IsNullOrEmpty(appConfigEndpoint))
{
    // If App Configuration is unreachable at startup (auth, network, permissions), start anyway with
    // flags OFF (rules enrichment) instead of crash-looping. A non-critical dependency must never take
    // down message processing. The error goes to stderr, which the Functions host forwards to its logs.
    try
    {
        config.AddAzureAppConfiguration(o => o
            .Connect(new Uri(appConfigEndpoint), credential)
            .UseFeatureFlags(ff => ff.SetRefreshInterval(TimeSpan.FromSeconds(30))));
        builder.Services.AddAzureAppConfiguration();
    }
    catch (Exception ex)
    {
        var failed = config.Sources.LastOrDefault(s => s.GetType().Name.Contains("AzureAppConfiguration"));
        if (failed is not null) config.Sources.Remove(failed);
        Console.Error.WriteLine(
            $"WARNING DealFlow.Worker: App Configuration unavailable at startup; feature flags default to OFF. {ex.GetType().Name}: {ex.Message}");
    }
}

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services.AddFeatureManagement();
builder.Services.AddSingleton<IFeatureFlags, FeatureManagerFlags>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton(_ => new CosmosClient(config["Cosmos:Endpoint"], credential, CosmosDealRepository.ClientOptions));
builder.Services.AddSingleton<IDealRepository>(sp => new CosmosDealRepository(
    sp.GetRequiredService<CosmosClient>().GetContainer(config["Cosmos:Database"], config["Cosmos:Container"])));

builder.Services.AddSingleton(_ => new AzureOpenAIClient(new Uri(config["AzureOpenAI:Endpoint"]!), credential));
builder.Services.AddSingleton<IDealEnricher>(sp => new AzureOpenAiDealEnricher(
    sp.GetRequiredService<AzureOpenAIClient>(), config["AzureOpenAI:Deployment"]!));

builder.Services.AddSingleton<RuleBasedEnricher>();
builder.Services.AddSingleton<DealProcessor>();

builder.Build().Run();
