@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param principalId string

resource final_registry 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: 'myacr'
}

resource final_registry_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(final_registry.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: final_registry
}