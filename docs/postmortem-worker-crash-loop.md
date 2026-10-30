# Postmortem: deals accepted but never processed (worker crash loop)

**Dates:** 2026-10-06 → 2026-10-08 (dev environment)  ·  **Impact:** 2 deals accepted (202) but not processed for ~2 days. No data lost.

## Symptoms
- `POST /deals` returned `202 Accepted`, but `GET /deals/{id}` kept returning `404`.
- Service Bus queue `deals`: `activeMessageCount` growing, `deadLetterMessageCount` 0 → messages were never even attempted.
- `az functionapp function list` returned nothing → the Functions host had not registered `ProcessDeal`.

## Timeline (UTC)
| Time | Event |
|---|---|
| Oct 6 05:44 | First worker deploy. Host logs start: *"Exceeded language worker restart retry count for runtime:dotnet-isolated"* |
| Oct 6 05:44 → 20:02 | Same message **492 times** — the .NET worker process crashes at startup on every restart |
| Oct 6 (afternoon) | Root cause found and fixed (below); redeploy reports "Deployment was successful", but `syncfunctiontriggers` still fails and no instance starts |
| Oct 6 20:02 → Oct 8 22:09 | No host activity: Flex Consumption had scaled to zero and nothing woke it, because the trigger was never synced |
| Oct 8 22:09:42 | Opening the Function App in the portal (a *List host keys* call) starts a fresh instance with the fixed package |
| Oct 8 22:10:34–35 | `Executed 'Functions.ProcessDeal' (Succeeded)` ×2 — both waiting deals processed, enriched by Azure OpenAI |

**Evidence**

![Host logs: 492 crash-loop messages on Oct 6, then host starts and two successful executions on Oct 8](screenshots/17-incident-kql-crash-loop.png)

![Activity Log: List host keys at 22:09:53 UTC, the request that started a fresh worker instance](screenshots/18-incident-activity-log-wake.png)

## Root cause
Reproducing the worker startup locally surfaced:

```
System.TypeLoadException: Could not load type
'Microsoft.ApplicationInsights.Extensibility.ITelemetryInitializer'
from assembly 'Microsoft.ApplicationInsights, Version=3.1.2'
   at ...ConfigureFunctionsApplicationInsights(...)
```

`Microsoft.ApplicationInsights.WorkerService` had been added without a version pin, so NuGet resolved **3.1.2**. The Functions bridge `Microsoft.Azure.Functions.Worker.ApplicationInsights` is built against **2.x**, which still has `ITelemetryInitializer`; 3.x removed it. The code compiled (our code never touches the type) and unit tests passed (they don't run Functions startup), so it only failed at runtime in Azure. Because the crash happened while telemetry was being configured, nothing reached Application Insights' `exceptions` table — only the host's restart messages did.

## Fixes
1. **Pinned** `Microsoft.ApplicationInsights.WorkerService` to `2.23.0` (the actual fix).
2. **Made App Configuration non-fatal at startup** — a feature-flag store outage now disables AI enrichment (rules fallback) instead of crash-looping the worker. A non-critical dependency must never stop message processing.
3. **Post-deploy verification in CI/CD** — the pipeline wakes the host and fails the deploy unless `ProcessDeal` is registered, then runs an end-to-end smoke test.

## What went well
- Service Bus held both messages for two days; once the worker recovered they were processed exactly once (idempotent processor), so **no deals were lost**.
- Queue metrics (active vs dead-letter) immediately located the fault on the consumer side.

## Lessons
- "Deployment successful" ≠ "service healthy". Verify the thing users depend on (trigger registered, message processed).
- Pin major versions of SDKs that other packages depend on.
- A crash *during* telemetry setup is invisible to telemetry — check host logs, and reproduce startup locally.
