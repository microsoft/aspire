@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param env_acr_pull_identity_outputs_id string

param env_acr_pull_identity_outputs_clientid string

param env_acr_outputs_name string

resource env_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: env_acr_outputs_name
}

resource env_asplan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: take('envasplan-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    perSiteScaling: true
    reserved: true
  }
  kind: 'Linux'
  sku: {
    name: 'P0V3'
    tier: 'Premium'
  }
}

output name string = env_asplan.name

output planId string = env_asplan.id

output webSiteSuffix string = uniqueString(resourceGroup().id)

output AZURE_CONTAINER_REGISTRY_NAME string = env_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = env_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = env_acr_pull_identity_outputs_id

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID string = env_acr_pull_identity_outputs_clientid