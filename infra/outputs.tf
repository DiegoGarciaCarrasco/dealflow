output "resource_group" {
  value = azurerm_resource_group.this.name
}

output "api_app_name" {
  value = azurerm_linux_web_app.api.name
}

output "api_url" {
  value = "https://${azurerm_linux_web_app.api.default_hostname}"
}

output "function_app_name" {
  value = azurerm_function_app_flex_consumption.worker.name
}

output "app_configuration_name" {
  value = azurerm_app_configuration.this.name
}