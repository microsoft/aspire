@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existing_env_name string

param existing_env_rg string

resource test_container_app_env 'Microsoft.App/managedEnvironments@2025-07-01' existing = {
  name: existing_env_name
  scope: resourceGroup(existing_env_rg)
}