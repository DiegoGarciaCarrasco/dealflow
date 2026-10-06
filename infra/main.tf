resource "random_string" "suffix" {
  length  = 5
  upper   = false
  special = false
}

locals {
  name = "dealflow-${var.environment}-${random_string.suffix.result}"
  # Storage account names: 3-24 chars, lowercase letters and numbers only.
  storage_name = "stdealflow${var.environment}${random_string.suffix.result}"
  tags = {
    app         = "dealflow"
    environment = var.environment
    managed_by  = "terraform"
  }

  # Cosmos DB built-in data-plane roles.
  cosmos_data_reader      = "00000000-0000-0000-0000-000000000001"
  cosmos_data_contributor = "00000000-0000-0000-0000-000000000002"
}

resource "azurerm_resource_group" "this" {
  name     = "rg-${local.name}"
  location = var.location
  tags     = local.tags
}

# ---------------------------------------------------------------- Observability

resource "azurerm_log_analytics_workspace" "this" {
  name                = "log-${local.name}"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = local.tags
}

resource "azurerm_application_insights" "this" {
  name                = "appi-${local.name}"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  workspace_id        = azurerm_log_analytics_workspace.this.id
  application_type    = "web"
  tags                = local.tags
}

# ---------------------------------------------------------------- Messaging

resource "azurerm_servicebus_namespace" "this" {
  name                = "sb-${local.name}"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  sku                 = "Standard" # Basic has no duplicate detection
  local_auth_enabled  = false      # Entra ID only; no SAS keys
  tags                = local.tags
}

resource "azurerm_servicebus_queue" "deals" {
  name         = "deals"
  namespace_id = azurerm_servicebus_namespace.this.id

  requires_duplicate_detection            = true
  duplicate_detection_history_time_window = "PT10M"
  max_delivery_count                      = 5
  dead_lettering_on_message_expiration    = true
  lock_duration                           = "PT1M"
}

# ---------------------------------------------------------------- Data

resource "azurerm_cosmosdb_account" "this" {
  name                         = "cosmos-${local.name}"
  location                     = azurerm_resource_group.this.location
  resource_group_name          = azurerm_resource_group.this.name
  offer_type                   = "Standard"
  kind                         = "GlobalDocumentDB"
  local_authentication_enabled = false # Entra ID only; no account keys

  capabilities {
    name = "EnableServerless"
  }

  consistency_policy {
    consistency_level = "Session"
  }

  geo_location {
    location          = azurerm_resource_group.this.location
    failover_priority = 0
  }

  tags = local.tags
}

resource "azurerm_cosmosdb_sql_database" "this" {
  name                = "dealflow"
  resource_group_name = azurerm_resource_group.this.name
  account_name        = azurerm_cosmosdb_account.this.name
}

resource "azurerm_cosmosdb_sql_container" "deals" {
  name                = "deals"
  resource_group_name = azurerm_resource_group.this.name
  account_name        = azurerm_cosmosdb_account.this.name
  database_name       = azurerm_cosmosdb_sql_database.this.name
  partition_key_paths = ["/id"] # see docs/adr/0002
}

# ---------------------------------------------------------------- AI

resource "azurerm_cognitive_account" "openai" {
  name                          = "oai-${local.name}"
  location                      = var.openai_location
  resource_group_name           = azurerm_resource_group.this.name
  kind                          = "OpenAI"
  sku_name                      = "S0"
  custom_subdomain_name         = "oai-${local.name}" # required for Entra ID auth
  local_auth_enabled            = false
  public_network_access_enabled = true
  tags                          = local.tags
}

resource "azurerm_cognitive_deployment" "chat" {
  name                 = "chat"
  cognitive_account_id = azurerm_cognitive_account.openai.id

  model {
    format  = "OpenAI"
    name    = var.openai_model
    version = var.openai_model_version
  }

  sku {
    name     = "GlobalStandard"
    capacity = 10 # thousands of tokens per minute
  }
}

# ---------------------------------------------------------------- Feature flags

resource "azurerm_app_configuration" "this" {
  name                = "appcs-${local.name}"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  sku                 = "free"
  local_auth_enabled  = false
  tags                = local.tags
}

# The identity running Terraform needs data-plane rights to write the flag.
resource "azurerm_role_assignment" "deployer_appconfig" {
  scope                = azurerm_app_configuration.this.id
  role_definition_name = "App Configuration Data Owner"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_app_configuration_feature" "ai_enrichment" {
  configuration_store_id = azurerm_app_configuration.this.id
  name                   = "AiEnrichment"
  description            = "LLM prioritization and summaries for incoming deals. Off = deterministic rules."
  enabled                = var.ai_enrichment_enabled

  lifecycle {
    # After the first apply the flag is operated at runtime (portal/CLI), not by Terraform.
    ignore_changes = [enabled]
  }

  depends_on = [azurerm_role_assignment.deployer_appconfig]
}

# ---------------------------------------------------------------- API (App Service)

resource "azurerm_service_plan" "api" {
  name                = "asp-api-${local.name}"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  os_type             = "Linux"
  sku_name            = "B1"
  tags                = local.tags
}

resource "azurerm_linux_web_app" "api" {
  name                = "app-api-${local.name}"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  service_plan_id     = azurerm_service_plan.api.id
  https_only          = true

  identity {
    type = "SystemAssigned"
  }

  site_config {
    always_on                         = true
    health_check_path                 = "/health"
    health_check_eviction_time_in_min = 5

    application_stack {
      dotnet_version = "8.0"
    }
  }

  app_settings = {
    APPLICATIONINSIGHTS_CONNECTION_STRING = azurerm_application_insights.this.connection_string
    ServiceBus__FullyQualifiedNamespace   = "${azurerm_servicebus_namespace.this.name}.servicebus.windows.net"
    ServiceBus__QueueName                 = azurerm_servicebus_queue.deals.name
    Cosmos__Endpoint                      = azurerm_cosmosdb_account.this.endpoint
    Cosmos__Database                      = azurerm_cosmosdb_sql_database.this.name
    Cosmos__Container                     = azurerm_cosmosdb_sql_container.deals.name
  }

  tags = local.tags
}

# ---------------------------------------------------------------- Worker (Azure Functions)

resource "azurerm_storage_account" "functions" {
  name                            = substr(local.storage_name, 0, 24)
  location                        = azurerm_resource_group.this.location
  resource_group_name             = azurerm_resource_group.this.name
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  allow_nested_items_to_be_public = false
  tags                            = local.tags
}

# Flex Consumption runs the app from a package stored in this blob container.
resource "azurerm_storage_container" "deployments" {
  name                  = "app-package"
  storage_account_id    = azurerm_storage_account.functions.id
  container_access_type = "private"
}

# Flex Consumption (FC1): serverless, scales per queue depth, pay per execution.
# Replaces Linux Consumption (Y1), which is legacy and could not be created alongside the API's plan.
resource "azurerm_service_plan" "functions" {
  name                = "asp-func-${local.name}"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  os_type             = "Linux"
  sku_name            = "FC1"
  tags                = local.tags
}

resource "azurerm_function_app_flex_consumption" "worker" {
  name                = "func-${local.name}"
  location            = azurerm_resource_group.this.location
  resource_group_name = azurerm_resource_group.this.name
  service_plan_id     = azurerm_service_plan.functions.id
  https_only          = true

  storage_container_type      = "blobContainer"
  storage_container_endpoint  = "${azurerm_storage_account.functions.primary_blob_endpoint}${azurerm_storage_container.deployments.name}"
  storage_authentication_type = "StorageAccountConnectionString"
  storage_access_key          = azurerm_storage_account.functions.primary_access_key # host storage only

  runtime_name           = "dotnet-isolated"
  runtime_version        = "8.0"
  maximum_instance_count = 40
  instance_memory_in_mb  = 2048

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_insights_connection_string = azurerm_application_insights.this.connection_string
  }

  app_settings = {
    ServiceBus__fullyQualifiedNamespace = "${azurerm_servicebus_namespace.this.name}.servicebus.windows.net"
    ServiceBusQueueName                 = azurerm_servicebus_queue.deals.name
    Cosmos__Endpoint                    = azurerm_cosmosdb_account.this.endpoint
    Cosmos__Database                    = azurerm_cosmosdb_sql_database.this.name
    Cosmos__Container                   = azurerm_cosmosdb_sql_container.deals.name
    AzureOpenAI__Endpoint               = azurerm_cognitive_account.openai.endpoint
    AzureOpenAI__Deployment             = azurerm_cognitive_deployment.chat.name
    AppConfig__Endpoint                 = azurerm_app_configuration.this.endpoint
  }

  tags = local.tags
}

# ---------------------------------------------------------------- Least-privilege identity wiring

resource "azurerm_role_assignment" "api_sb_sender" {
  scope                = azurerm_servicebus_queue.deals.id
  role_definition_name = "Azure Service Bus Data Sender"
  principal_id         = azurerm_linux_web_app.api.identity[0].principal_id
}

resource "azurerm_cosmosdb_sql_role_assignment" "api_cosmos_reader" {
  resource_group_name = azurerm_resource_group.this.name
  account_name        = azurerm_cosmosdb_account.this.name
  role_definition_id  = "${azurerm_cosmosdb_account.this.id}/sqlRoleDefinitions/${local.cosmos_data_reader}"
  principal_id        = azurerm_linux_web_app.api.identity[0].principal_id
  scope               = azurerm_cosmosdb_account.this.id
}

resource "azurerm_role_assignment" "worker_sb_receiver" {
  scope                = azurerm_servicebus_queue.deals.id
  role_definition_name = "Azure Service Bus Data Receiver"
  principal_id         = azurerm_function_app_flex_consumption.worker.identity[0].principal_id
}

resource "azurerm_cosmosdb_sql_role_assignment" "worker_cosmos_contributor" {
  resource_group_name = azurerm_resource_group.this.name
  account_name        = azurerm_cosmosdb_account.this.name
  role_definition_id  = "${azurerm_cosmosdb_account.this.id}/sqlRoleDefinitions/${local.cosmos_data_contributor}"
  principal_id        = azurerm_function_app_flex_consumption.worker.identity[0].principal_id
  scope               = azurerm_cosmosdb_account.this.id
}

resource "azurerm_role_assignment" "worker_openai_user" {
  scope                = azurerm_cognitive_account.openai.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_function_app_flex_consumption.worker.identity[0].principal_id
}

resource "azurerm_role_assignment" "worker_appconfig_reader" {
  scope                = azurerm_app_configuration.this.id
  role_definition_name = "App Configuration Data Reader"
  principal_id         = azurerm_function_app_flex_consumption.worker.identity[0].principal_id
}
