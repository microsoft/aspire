@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

resource messaging 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: existingResourceName
}

resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  name: 'queue'
  parent: messaging
}

output serviceBusEndpoint string = messaging.properties.serviceBusEndpoint

output name string = existingResourceName