@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingEventHubName string

resource eventhubs 'Microsoft.EventHub/namespaces@2024-01-01' existing = {
  name: existingEventHubName
}

resource myhub 'Microsoft.EventHub/namespaces/eventhubs@2024-01-01' = {
  name: 'myhub'
  parent: eventhubs
}

output eventHubsEndpoint string = eventhubs.properties.serviceBusEndpoint

output eventHubsHostName string = split(replace(eventhubs.properties.serviceBusEndpoint, 'https://', ''), ':')[0]

output name string = eventhubs.name