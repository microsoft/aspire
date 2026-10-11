@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param mystorage_outputs_name string

param principalId string

resource mystorage 'Microsoft.Storage/storageAccounts@2025-06-01' existing = {
  name: mystorage_outputs_name
}

resource mystorage_StorageAccountContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(mystorage.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '17d1049b-9a84-46fb-8f53-869881c3d3ab'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '17d1049b-9a84-46fb-8f53-869881c3d3ab')
    principalType: 'ServicePrincipal'
  }
  scope: mystorage
}