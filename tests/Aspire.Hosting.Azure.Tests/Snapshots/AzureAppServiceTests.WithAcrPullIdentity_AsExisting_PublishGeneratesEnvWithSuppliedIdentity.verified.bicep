@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param shared_mi_outputs_id string

param shared_mi_outputs_clientid string

param registryName string

param sharedRg string

param appServicePlanName string

resource acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: registryName
  scope: resourceGroup(sharedRg)
}

resource env 'Microsoft.Web/serverfarms@2025-03-01' existing = {
  name: appServicePlanName
  scope: resourceGroup(sharedRg)
}

output name string = env.name

output planId string = env.id

output webSiteSuffix string = uniqueString(resourceGroup().id)

output AZURE_CONTAINER_REGISTRY_NAME string = acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = shared_mi_outputs_id

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID string = shared_mi_outputs_clientid
