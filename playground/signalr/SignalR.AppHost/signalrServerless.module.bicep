@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource signalrServerless 'Microsoft.SignalRService/signalR@2024-03-01' = {
  name: take('signalrServerless-${uniqueString(resourceGroup().id)}', 63)
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
        value: 'Serverless'
      }
    ]
  }
  kind: 'SignalR'
  sku: {
    name: 'Free_F1'
    capacity: 1
  }
  tags: {
    'aspire-resource-name': 'signalrServerless'
  }
}

output hostName string = signalrServerless.properties.hostName

output name string = signalrServerless.name

output id string = signalrServerless.id