using System.Text.Json;
using Azure.Messaging.ServiceBus;
using DealFlow.Core;

namespace DealFlow.Api;

public sealed class ServiceBusDealPublisher(ServiceBusSender sender) : IDealPublisher
{
    public Task PublishAsync(Deal deal, CancellationToken ct = default)
    {
        var message = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(deal, CosmosDealRepository.JsonOptions))
        {
            // MessageId = deal id lets Service Bus duplicate detection drop client retries (see docs/adr/0001).
            MessageId = deal.Id,
            ContentType = "application/json",
            Subject = "deal.received",
        };
        return sender.SendMessageAsync(message, ct);
    }
}
