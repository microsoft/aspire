@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param acr_outputs_name string

param principalId string

resource acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: acr_outputs_name
}

resource acr_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: acr
}