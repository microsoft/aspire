@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param environmentName string

param sharedRg string

param env_acr_pull_identity_outputs_id string

param env_acr_outputs_name string

resource env 'Microsoft.App/managedEnvironments@2025-07-01' existing = {
  name: environmentName
  scope: resourceGroup(sharedRg)
}

resource env_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: env_acr_outputs_name
}

output AZURE_CONTAINER_REGISTRY_NAME string = env_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = env_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = env_acr_pull_identity_outputs_id

output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = env.name

output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = env.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = env.properties.defaultDomain