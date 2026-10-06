# ADR 0003: LLM enrichment behind a runtime feature flag, with a deterministic fallback

**Status:** Accepted

## Context
Deals are enriched with a priority and one-line summary by an Azure OpenAI model. LLMs add risks a normal dependency doesn't: throttling (429s), latency spikes, cost per call, and output that may be malformed or wrong.

## Decision
1. **Feature flag.** `AiEnrichment` lives in Azure App Configuration and the worker reads it via `Microsoft.FeatureManagement` (refreshed every 30 s). The AI path can be turned off, or rolled out gradually with a percentage filter, without redeploying.
2. **Fallback.** If the flag is off *or* the model call fails, `RuleBasedEnricher` assigns priority by deal size. A valid deal is never dead-lettered just because the model had a bad moment. `EnrichedBy` records which path produced each result.
3. **Output validation.** The model is asked for strict JSON (`response_format: json_object`, temperature 0). `AzureOpenAiDealEnricher.Parse` rejects unknown priorities and empty summaries, and caps length; a rejection triggers the fallback. Deal notes are passed as data and the system prompt tells the model not to follow instructions inside them.

## Consequences
- Two code paths to maintain and test (both covered in `DealProcessorTests`).
- Comparing `EnrichedBy = "ai"` vs `"rules"` in Application Insights gives a live view of fallback rate, a useful alert signal.
- Next step if this were production: log a sample of AI results for human review to measure quality, not just availability.
