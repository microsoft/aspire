@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sku string = 'Premium'

resource servicebus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: take('servicebus-${uniqueString(resourceGroup().id)}', 50)
  location: location
  properties: {
    disableLocalAuth: true
    publicNetworkAccess: 'Disabled'
  }
  sku: {
    name: sku
  }
  tags: {
    'aspire-resource-name': 'servicebus'
  }
}

output serviceBusEndpoint string = servicebus.properties.serviceBusEndpoint

output serviceBusHostName string = split(replace(servicebus.properties.serviceBusEndpoint, 'https://', ''), ':')[0]

output name string = servicebus.name

output id string = servicebus.id