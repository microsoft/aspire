@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sku string = 'Free_F1'

param capacity int = 1

resource webpubsub 'Microsoft.SignalRService/webPubSub@2024-03-01' = {
  name: take('webpubsub-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    disableLocalAuth: true
    publicNetworkAccess: 'Disabled'
  }
  sku: {
    name: sku
    capacity: capacity
  }
  tags: {
    'aspire-resource-name': 'webpubsub'
  }
}

output endpoint string = 'https://${webpubsub.properties.hostName}'

output name string = webpubsub.name

output id string = webpubsub.id