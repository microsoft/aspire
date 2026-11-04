@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param cluster_name string

param supporting_subscription string

param cluster_group string

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-01' existing = {
  name: cluster_name
}

resource cluster_reader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: '00000000-0000-0000-0000-000000000003'
  properties: {
    principalId: '00000000-0000-0000-0000-000000000004'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
    principalType: 'ServicePrincipal'
  }
  scope: aks
}