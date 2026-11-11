@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param principalId string

resource network 'Microsoft.Network/virtualNetworks@2025-05-01' existing = {
  name: 'existingvnet'
}

resource network_NetworkContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(network.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4d97b98b-1d4f-4787-a291-c67834d212e7'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4d97b98b-1d4f-4787-a291-c67834d212e7')
    principalType: 'ServicePrincipal'
  }
  scope: network
}