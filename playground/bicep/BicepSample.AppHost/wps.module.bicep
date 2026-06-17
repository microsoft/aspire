@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sku string = 'Free_F1'

param capacity int = 1

resource wps 'Microsoft.SignalRService/webPubSub@2024-03-01' = {
  name: take('wps-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    disableLocalAuth: true
  }
  sku: {
    name: sku
    capacity: capacity
  }
  tags: {
    'aspire-resource-name': 'wps'
  }
}

output endpoint string = 'https://${wps.properties.hostName}'

output name string = wps.name

output id string = wps.id