@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sku string = 'Free_F1'

param capacity int = 1

param hub2_url_0 string

param hub2_url_1 string

param hub2_url_2 string

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

resource hub1 'Microsoft.SignalRService/webPubSub/hubs@2024-03-01' = {
  name: 'hub1'
  properties: {
    eventHandlers: [
      {
        urlTemplate: 'http://fake2.com'
        userEventPattern: 'event1'
      }
      {
        urlTemplate: 'http://fake3.com'
        userEventPattern: '*'
        systemEvents: [
          'connect'
        ]
        auth: {
          type: 'ManagedIdentity'
          managedIdentity: {
            resource: 'abc'
          }
        }
      }
      {
        urlTemplate: 'http://fake1.com'
      }
    ]
    anonymousConnectPolicy: 'allow'
  }
  parent: wps1
}

resource hub2 'Microsoft.SignalRService/webPubSub/hubs@2024-03-01' = {
  name: 'hub2'
  properties: {
    eventHandlers: [
      {
        urlTemplate: hub2_url_0
        userEventPattern: '*'
      }
      {
        urlTemplate: hub2_url_1
        userEventPattern: '*'
      }
      {
        urlTemplate: hub2_url_2
        userEventPattern: 'event1'
        systemEvents: [
          'connect'
          'connected'
        ]
      }
    ]
  }
  parent: wps1
}

output endpoint string = 'https://${wps1.properties.hostName}'

output name string = wps1.name

output id string = wps1.id