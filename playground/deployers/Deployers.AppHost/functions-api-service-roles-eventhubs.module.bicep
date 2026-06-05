@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingEventHubName string

param principalId string

resource eventhubs 'Microsoft.EventHub/namespaces@2024-01-01' existing = {
  name: existingEventHubName
}

resource eventhubs_AzureEventHubsDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(eventhubs.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'f526a384-b230-433a-b45c-95f59c4a2dec'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'f526a384-b230-433a-b45c-95f59c4a2dec')
    principalType: 'ServicePrincipal'
  }
  scope: eventhubs
}