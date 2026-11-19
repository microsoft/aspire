@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param wps1_outputs_name string

param abc_url_0 string

resource wps1 'Microsoft.SignalRService/webPubSub@2024-03-01' existing = {
  name: wps1_outputs_name
}

resource abc 'Microsoft.SignalRService/webPubSub/hubs@2024-03-01' = {
  name: 'abc'
  properties: {
    eventHandlers: [
      {
        urlTemplate: abc_url_0
        userEventPattern: '*'
      }
    ]
  }
  parent: wps1
}