# How DealFlow was built

DealFlow went from a job-description gap analysis to a CI/CD-deployed, AI-enriched, event-driven service on Azure in about a month (Sep 30 – Oct 30, 2026). This page tells that story in order, including the problems hit along the way, because the fixes say more about the engineering than the happy path does.

**Contents:** [1. Why](#1-why-this-project) · [2. Design](#2-design-before-code) · [3. Code](#3-code-inside-out) · [4. First deploy](#4-first-manual-deploy) · [5. Incident](#5-the-incident) · [6. Verify](#6-understand-and-verify) · [7. CI/CD](#7-cicd-with-oidc) · [8. Web UI](#8-web-ui) · [9. Public-repo hardening](#9-public-repo-hardening) · [10. The result](#10-the-result)

---

## 1. Why this project
I compared my resume with a Microsoft Software Engineer posting (Commercial Engineering & AI). I had two years of infrastructure-as-code on Azure, but little proof of **application-level backend work**: REST APIs, messaging patterns (Service Bus), Cosmos DB, telemetry, safe rollout, and AI integration. I decided to build one project that would close exactly that gap, deployed and operated like a real service rather than run on localhost.

## 2. Design before code
- **Scenario:** sales deal intake with AI triage, so the project reads as a product, not a tutorial.
- **Pattern:** async request-reply. The API accepts fast and returns `202`; a worker does the slow AI and database work; the client polls.
- **Decisions recorded as ADRs:**
  - [0001](adr/0001-service-bus-for-deal-handoff.md): Service Bus over Storage Queues and Event Hubs, for dead-lettering and duplicate detection.
  - [0002](adr/0002-cosmos-partitioning.md): Cosmos DB serverless, partitioned by `/id` for cheap point reads.
  - [0003](adr/0003-ai-enrichment-feature-flag.md): Azure OpenAI behind a runtime feature flag, with output validation and a rules fallback.
- **Security baseline from day one:** managed identities everywhere and local/key auth disabled on every service.

## 3. Code, inside out
The order was deliberate: business logic first, Azure at the edges.

1. **Core domain** (`src/DealFlow.Core`): `Deal`, validation, interfaces, then `DealProcessor` (idempotency check → AI or rules → save), with unit tests. No Azure SDK involved, so every branch is testable in memory.
2. **API** (`src/DealFlow.Api`): ASP.NET Core minimal API on top of Core, publishing to Service Bus.
3. **Worker** (`src/DealFlow.Worker`): Azure Functions isolated worker with a Service Bus trigger and the Azure OpenAI enricher (strict JSON, temperature 0, schema validation, prompt-injection mitigation).
4. **Infrastructure** (`infra/`): Terraform for ~25 resources plus least-privilege role assignments.

![Tests and smoke test passing](screenshots/19-tests-and-smoke-test.png)

## 4. First manual deploy
I deployed by hand first (Terraform from my laptop, zip deploy with the Azure CLI) so I'd understand every step before automating it.

![Terraform plan](screenshots/24-terraform-plan.png)

**Problem: the worker's hosting plan was rejected.** `Requested features 'Dynamic SKU, Linux Worker' not available in resource group`. The legacy Linux Consumption plan (Y1) couldn't share the resource group's infrastructure with the API's B1 plan. **Fix:** moved the worker to **Flex Consumption** (FC1), Microsoft's current serverless Functions plan, with its own deployment-package container.

![Y1 plan rejected](screenshots/26-problem-y1-plan-rejected.png)

The API came up and accepted deals.

## 5. The incident
Deals were accepted (`202`) but never processed. Over two days I triaged it from queue metrics, the function list, and host logs; reproduced the worker startup locally; and found a `TypeLoadException` caused by an Application Insights SDK major-version conflict. Service Bus held the messages throughout, and both deals were processed exactly once after recovery.

![Crash loop and recovery in host logs](screenshots/17-incident-kql-crash-loop.png)

Full write-up: **[Postmortem: worker crash loop](postmortem-worker-crash-loop.md)**.

## 6. Understand and verify
Before adding features I made sure I understood and could prove the system:
- Traced the full request path and the Azure OpenAI pipeline end to end.
- Wrote `scripts/smoke-test.sh`: health, validation, worker registration, three real deals through the whole pipeline, and queue health.

![Healthy scale from zero](screenshots/20-appinsights-healthy-scale-from-zero.png)

Where each piece lives in Azure:

| | |
|---|---|
| ![Function invocations](screenshots/08-functions-processdeal-invocations.png) | ![Service Bus metrics](screenshots/09a-servicebus-queue-metrics.png) |
| Worker invocations | Queue metrics: 0 dead-letters |
| ![Service Bus properties](screenshots/09b-servicebus-queue-properties.png) | ![Keyless Service Bus](screenshots/10-servicebus-keyless-auth.png) |
| Duplicate detection, max delivery 5 | Local auth disabled |
| ![Cosmos document](screenshots/11-cosmos-processed-deal.png) | ![OpenAI deployment](screenshots/12-openai-chat-deployment.png) |
| A processed deal in Cosmos DB | `chat` = gpt-4.1-mini, 10K TPM |
| ![Feature flag](screenshots/13-appconfig-feature-flag.png) | ![Managed identity](screenshots/14a-functions-managed-identity.png) |
| `AiEnrichment` flag | Worker managed identity |
| ![OpenAI RBAC](screenshots/14b-openai-rbac-role-assignments.png) | ![End-to-end trace](screenshots/16-appinsights-end-to-end-trace.png) |
| Worker's least-privilege OpenAI role | One deal, end to end, including the OpenAI call |

![Application map](screenshots/15-appinsights-application-map.png)

## 7. CI/CD with OIDC
I destroyed the environment and rebuilt it from scratch through GitHub Actions to prove the pipeline could stand it up alone.

- `scripts/setup-github-oidc.sh` creates an app registration trusted only by this repo's `dev` environment (a federated credential), so the pipeline has **no stored password**.
- The workflow: build and test → Terraform validate → `terraform apply` → deploy API and worker → verify the worker registered → end-to-end smoke test.

![OIDC federated credential](screenshots/06-entra-oidc-federated-credential.png)

**Problems fixed along the way:**

| Failure | Cause | Fix |
|---|---|---|
| `403 AuthorizationPermissionMismatch` reading Terraform state | Sign-in worked, but the pipeline identity had no **data-plane** role on the state storage (Contributor doesn't cover blob data) | Granted *Storage Blob Data Contributor* on the state account |
| `Cannot find required .azurefunctions directory` | `actions/upload-artifact` skips hidden folders, so the Functions metadata never reached the deploy job | `include-hidden-files: true` |
| `state blob is already locked` | A re-run and a new push overlapped on the same state | `concurrency: deploy-dev` so runs queue |
| `409 RoleAssignmentExists` | Roles I'd granted by hand collided with the same roles now defined in Terraform | Removed the manual ones; my portal access now lives in `infra/admin-access.tf` |
| `Request blocked by Auth … readMetadata` in Data Explorer | A rebuild deletes per-resource roles, and Owner isn't enough with key auth off | Same `admin-access.tf`, so access is reproducible |

![State 403](screenshots/28-problem-actions-state-403.png)
![Missing .azurefunctions](screenshots/29-problem-azurefunctions-missing.png)
![Cosmos RBAC blocked](screenshots/30-problem-cosmos-rbac-blocked.png)

The green run:

![Pipeline run](screenshots/04-actions-pipeline-run.png)
![Deploy steps](screenshots/05-actions-deploy-steps.png)
![Resource group](screenshots/07-azure-resource-group.png)

## 8. Web UI
A single static page served by the API from `wwwroot/` (same origin, so no CORS and no extra Azure resource). It makes the async design visible: a live pipeline strip, the deal as entered next to what Azure OpenAI added, and a history of processed deals.

Backend changes it needed: a `GET /deals?limit=N` endpoint (a capped, parameterized Cosmos query; its cross-partition cost is noted in ADR 0002) and a per-IP rate limit on `POST /deals`, because a public form can be spammed and each deal costs an OpenAI call. Three integration tests cover the page, the list ordering, and the `429` path.

| | |
|---|---|
| ![Deal result](screenshots/01-ui-deal-result.png) | ![In progress](screenshots/21-ui-pipeline-in-progress.png) |
| Entered vs AI output | Waiting on the worker |
| ![Processed deals](screenshots/02-ui-processed-deals.png) | ![Validation errors](screenshots/03-ui-validation-errors.png) |
| AI vs rules fallback, processing time | Server-side validation |
| ![Rate limit](screenshots/22-api-rate-limit-429.png) | ![List endpoint](screenshots/23-api-list-endpoint.png) |
| 10/min per IP, then `429` | `GET /deals?limit=2` |

## 9. Public-repo hardening
- Azure IDs moved to GitHub **secrets**, so they're masked as `***` in public Actions logs.
- My personal admin ID lives in a gitignored `infra/admin.auto.tfvars` locally and an `ADMIN_OBJECT_ID` secret in CI; only an `.example` is committed.
- The `dev` environment deploys only from `main`, so forks and pull requests can never reach Azure.
- Old workflow runs with unmasked IDs were deleted before publishing.

![Environment protection](screenshots/25-github-dev-environment-protection.png)

## 10. The result
- **Architecture:** REST API → Service Bus → Azure Functions → Azure OpenAI → Cosmos DB, with App Configuration flags and Application Insights telemetry.
- **Reliability:** idempotent at-least-once processing, dead-lettering, graceful AI fallback, non-fatal config startup, verified deployments.
- **Security:** no keys anywhere, least-privilege managed identities, OIDC CI/CD, rate limiting, XSS-safe rendering, validated model output.
- **Operations:** a real live-site incident, diagnosed and written up.

## How I used AI tools
<!-- Write this yourself, honestly: what you used (e.g., Claude for scaffolding and debugging help),
     what you reviewed, changed, or rejected, and what you verified by hand (deploying, diagnosing the
     incident, fixing each pipeline failure). -->
