@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sql_store_outputs_name string

param principalId string

resource sql_store 'Microsoft.Storage/storageAccounts@2024-01-01' existing = {
  name: sql_store_outputs_name
}

resource sql_store_StorageFileDataPrivilegedContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sql_store.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69566ab7-960f-475b-8e7c-b3118f30c6bd'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69566ab7-960f-475b-8e7c-b3118f30c6bd')
    principalType: 'ServicePrincipal'
  }
  scope: sql_store
}