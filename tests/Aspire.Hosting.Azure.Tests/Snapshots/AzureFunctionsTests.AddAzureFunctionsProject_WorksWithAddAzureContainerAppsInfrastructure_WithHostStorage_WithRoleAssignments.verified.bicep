@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param my_own_storage_outputs_name string

param principalId string

resource my_own_storage 'Microsoft.Storage/storageAccounts@2025-06-01' existing = {
  name: my_own_storage_outputs_name
}

resource my_own_storage_StorageBlobDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(my_own_storage.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
    principalType: 'ServicePrincipal'
  }
  scope: my_own_storage
}