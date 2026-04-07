@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource signalrDefault 'Microsoft.SignalRService/signalR@2024-03-01' = {
  name: take('signalrDefault-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    cors: {
      allowedOrigins: [
        '*'
      ]
    }
    disableLocalAuth: true
    features: [
      {
        flag: 'ServiceMode'
        value: 'Default'
      }
    ]
  }
  kind: 'SignalR'
  sku: {
    name: 'Free_F1'
    capacity: 1
  }
  tags: {
    'aspire-resource-name': 'signalrDefault'
  }
}

output hostName string = signalrDefault.properties.hostName

output name string = signalrDefault.name

output id string = signalrDefault.id