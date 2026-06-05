@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sku string = 'Standard'

resource eh 'Microsoft.EventHub/namespaces@2024-01-01' = {
  name: take('eh-${uniqueString(resourceGroup().id)}', 256)
  location: location
  properties: {
    disableLocalAuth: true
  }
  sku: {
    name: sku
  }
  tags: {
    'aspire-resource-name': 'eh'
  }
}

resource hub_resource 'Microsoft.EventHub/namespaces/eventhubs@2024-01-01' = {
  name: 'hub-name'
  properties: {
    partitionCount: 3
  }
  parent: eh
}

resource cg1 'Microsoft.EventHub/namespaces/eventhubs/consumergroups@2024-01-01' = {
  name: 'group-name'
  parent: hub_resource
}

output eventHubsEndpoint string = eh.properties.serviceBusEndpoint

output eventHubsHostName string = split(replace(eh.properties.serviceBusEndpoint, 'https://', ''), ':')[0]

output name string = eh.name
