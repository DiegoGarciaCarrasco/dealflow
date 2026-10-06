using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;

namespace DealFlow.Core;

public sealed class CosmosDealRepository(Container container) : IDealRepository
{
    /// <summary>Shared serializer settings so "Id" is stored as Cosmos's required "id" and enums are readable strings.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static CosmosClientOptions ClientOptions => new()
    {
        UseSystemTextJsonSerializerWithOptions = JsonOptions,
        ApplicationName = "dealflow",
    };

    public async Task<Deal?> GetAsync(string id, CancellationToken ct = default)
    {
        try
        {
            // Point read (id + partition key) — the cheapest Cosmos operation, ~1 RU.
            var response = await container.ReadItemAsync<Deal>(id, new PartitionKey(id), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task UpsertAsync(Deal deal, CancellationToken ct = default) =>
        container.UpsertItemAsync(deal, new PartitionKey(deal.Id), cancellationToken: ct);
}
