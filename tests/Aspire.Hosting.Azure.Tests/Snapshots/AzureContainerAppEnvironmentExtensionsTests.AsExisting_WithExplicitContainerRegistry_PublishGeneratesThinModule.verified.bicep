@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param environmentName string

param sharedRg string

param registryName string

resource env 'Microsoft.App/managedEnvironments@2025-07-01' existing = {
  name: environmentName
  scope: resourceGroup(sharedRg)
}

resource env_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('env_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: registryName
  scope: resourceGroup(sharedRg)
}

resource acr_env_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, env_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: env_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: acr
}

output AZURE_CONTAINER_REGISTRY_NAME string = acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = env_mi.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_NAME string = env.name

output AZURE_CONTAINER_APPS_ENVIRONMENT_ID string = env.id

output AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN string = env.properties.defaultDomain