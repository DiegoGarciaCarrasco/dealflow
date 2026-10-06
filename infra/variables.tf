variable "location" {
  description = "Azure region for all resources."
  type        = string
  default     = "westus2"
}

variable "environment" {
  description = "Short environment name used in resource names (dev, prod)."
  type        = string
  default     = "dev"
}

variable "openai_location" {
  description = "Region for Azure OpenAI (model availability varies by region)."
  type        = string
  default     = "eastus2"
}

variable "openai_model" {
  description = "Chat model to deploy. Check current availability/retirement dates before deploying."
  type        = string
  default     = "gpt-4.1-mini"
}

variable "openai_model_version" {
  type    = string
  default = "2025-04-14"
}

variable "ai_enrichment_enabled" {
  description = "Initial state of the AiEnrichment feature flag. Flip it in App Configuration at runtime afterwards."
  type        = bool
  default     = true
}
