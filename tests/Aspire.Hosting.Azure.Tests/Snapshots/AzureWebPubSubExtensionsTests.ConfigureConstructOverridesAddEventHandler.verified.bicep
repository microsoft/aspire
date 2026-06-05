@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sku string = 'Free_F1'

param capacity int = 1

resource wps1 'Microsoft.SignalRService/webPubSub@2024-03-01' = {
  name: take('wps1-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    disableLocalAuth: true
  }
  sku: {
    name: sku
    capacity: capacity
  }
  tags: {
    'aspire-resource-name': 'wps1'
  }
}

resource ABC 'Microsoft.SignalRService/webPubSub/hubs@2024-03-01' = {
  name: 'ABC'
  properties: {
    eventHandlers: [
      {
        urlTemplate: 'http://fake1.com'
        userEventPattern: '*'
      }
      {
        urlTemplate: 'http://fake.com'
      }
    ]
  }
  parent: wps1
}

output endpoint string = 'https://${wps1.properties.hostName}'

output name string = wps1.name

output id string = wps1.id