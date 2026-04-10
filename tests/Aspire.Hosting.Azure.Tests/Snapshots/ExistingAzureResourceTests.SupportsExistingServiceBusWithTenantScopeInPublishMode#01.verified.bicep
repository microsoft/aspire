@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

param existingSubscriptionId string

param principalType string

param principalId string

resource messaging 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: existingResourceName
  scope: subscription(existingSubscriptionId)
}

resource messaging_AzureServiceBusDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(messaging.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '090c5cfd-751d-490a-894a-3ce6f1109419'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '090c5cfd-751d-490a-894a-3ce6f1109419')
    principalType: principalType
  }
  scope: messaging
}