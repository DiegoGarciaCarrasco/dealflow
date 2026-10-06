# ADR 0001: Service Bus queue (not Storage Queues or Event Hubs) between the API and the worker

**Status:** Accepted

## Context
The API must accept deals quickly and hand them to slower work (an LLM call plus a database write). The handoff has to survive worker crashes, absorb bursts, and must not silently drop or double-process a deal.

## Options considered
| Option | Fit | Why not / why |
|---|---|---|
| Direct call from API to worker | Poor | Couples API latency and availability to Azure OpenAI; a slow model call becomes a slow POST. |
| Azure Storage Queues | OK | Cheap and simple, but no dead-letter queue, no duplicate detection, 64 KB messages. |
| Azure Event Hubs | Poor | Built for high-volume telemetry streams with consumer offsets, not per-message work items that need retries and dead-lettering. |
| **Azure Service Bus queue (Standard)** | **Chosen** | Dead-letter queue after `max_delivery_count` (5), duplicate detection on `MessageId`, peek-lock retries, Entra ID auth, native Functions trigger. |

## Decision
Use a Service Bus Standard queue named `deals`. The API sets `MessageId = deal.Id`, so client retries within the 10-minute duplicate-detection window are dropped by the broker.

## Consequences
- Delivery is at-least-once, so the worker must be idempotent. `DealProcessor` skips deals already marked `Processed` (tested in `Duplicate_delivery_is_idempotent`).
- Poison messages land in the dead-letter queue instead of blocking the queue; they need an alert and a runbook for replay.
- Standard tier has a small base cost versus Storage Queues. Acceptable for the reliability features.
