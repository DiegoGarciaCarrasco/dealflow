# ADR 0002: Cosmos DB serverless, partitioned by `/id`

**Status:** Accepted

## Context
Deals are small JSON documents. The dominant access pattern is "get one deal by id" (client polling `GET /deals/{id}` and the worker's idempotency check). Volume is low and spiky.

## Options considered
- **Partition key `/id`** (chosen): every read is a point read (~1 RU, the cheapest Cosmos operation), writes spread evenly across physical partitions, and there's no hot-partition risk.
- **`/region`**: groups deals for regional queries, but only 4 values means low cardinality and hot partitions as volume grows.
- **`/accountName`**: good for "all deals for an account," but large accounts become hot, and point reads would need the account name in the URL.

Capacity: **serverless** over provisioned throughput, because traffic is bursty and low, so paying per request beats paying for idle RU/s.

## Decision
Serverless account, `Session` consistency, container `deals` partitioned by `/id`. Account keys disabled; apps use managed identities with Cosmos data-plane RBAC (the API gets read-only, the worker gets read/write).

## Consequences
- Queries across deals (e.g., "all High priority deals in LATAM") are cross-partition fan-outs. The UI's `GET /deals?limit=N` (newest 20, max 50) accepts that cost because volume is tiny; a real reporting feature should use change feed to build a projection container keyed for its query.
- Serverless has per-container storage and throughput ceilings; move to autoscale provisioned throughput if sustained load appears.
