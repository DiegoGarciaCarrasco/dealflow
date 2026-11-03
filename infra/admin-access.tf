# -----------------------------------------------------------------------------------------------
# Optional: data-plane access for a human admin (you), so the portal works after every rebuild.
#
# Being subscription Owner is not enough: key/local auth is disabled on Cosmos DB, Service Bus and
# Azure OpenAI, so reading documents, peeking messages, or using the chat playground needs these
# data roles. Leave admin_object_id empty and nothing here is created.
#
# The value is NOT committed (public repo). Supply the same ID in both places, or a run without it
# will remove the roles a run with it created:
#   GitHub Actions: repository secret ADMIN_OBJECT_ID (passed as TF_VAR_admin_object_id)
#   Your Mac:       infra/admin.auto.tfvars (gitignored; copy admin.auto.tfvars.example)
# -----------------------------------------------------------------------------------------------

variable "admin_object_id" {
  description = "Entra object ID of a user to grant portal data access (az ad signed-in-user show --query id -o tsv). Empty = none."
  type        = string
  default     = ""
}

locals {
  grant_admin = var.admin_object_id != ""
}

# Cosmos DB Data Explorer: read and write documents.
resource "azurerm_cosmosdb_sql_role_assignment" "admin_cosmos_contributor" {
  count               = local.grant_admin ? 1 : 0
  resource_group_name = azurerm_resource_group.this.name
  account_name        = azurerm_cosmosdb_account.this.name
  role_definition_id  = "${azurerm_cosmosdb_account.this.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
  principal_id        = var.admin_object_id
  scope               = azurerm_cosmosdb_account.this.id
}

# Service Bus Explorer: peek, send, and inspect dead-lettered messages.
resource "azurerm_role_assignment" "admin_servicebus_owner" {
  count                = local.grant_admin ? 1 : 0
  scope                = azurerm_servicebus_namespace.this.id
  role_definition_name = "Azure Service Bus Data Owner"
  principal_id         = var.admin_object_id
  principal_type       = "User"
}

# Azure AI Foundry chat playground against the "chat" deployment.
resource "azurerm_role_assignment" "admin_openai_user" {
  count                = local.grant_admin ? 1 : 0
  scope                = azurerm_cognitive_account.openai.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = var.admin_object_id
  principal_type       = "User"
}

# App Configuration feature manager / configuration explorer: read and toggle the AiEnrichment flag.
resource "azurerm_role_assignment" "admin_appconfig_owner" {
  count                = local.grant_admin ? 1 : 0
  scope                = azurerm_app_configuration.this.id
  role_definition_name = "App Configuration Data Owner"
  principal_id         = var.admin_object_id
  principal_type       = "User"
}