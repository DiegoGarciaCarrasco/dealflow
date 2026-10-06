using System.Text.Json;
using Azure.Messaging.ServiceBus;
using DealFlow.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DealFlow.Worker;

public sealed class ProcessDealFunction(DealProcessor processor, ILogger<ProcessDealFunction> logger)
{
    /// <summary>
    /// Triggered per message on the "deals" queue. If this throws, Service Bus retries the message and,
    /// after MaxDeliveryCount attempts, moves it to the dead-letter queue for inspection.
    /// </summary>
    [Function(nameof(ProcessDeal))]
    public async Task ProcessDeal(
        [ServiceBusTrigger("%ServiceBusQueueName%", Connection = "ServiceBus")] ServiceBusReceivedMessage message,
        CancellationToken ct)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["DealId"] = message.MessageId,
            ["DeliveryCount"] = message.DeliveryCount,
        });

        var deal = JsonSerializer.Deserialize<Deal>(message.Body.ToMemory().Span, CosmosDealRepository.JsonOptions)
            ?? throw new InvalidOperationException($"Message {message.MessageId} has an empty body.");

        await processor.ProcessAsync(deal, ct);
    }
}
