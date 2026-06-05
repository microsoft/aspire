@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sku string = 'Standard'

resource sb 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: take('sb-${uniqueString(resourceGroup().id)}', 50)
  location: location
  properties: {
    disableLocalAuth: true
  }
  sku: {
    name: sku
  }
  tags: {
    'aspire-resource-name': 'sb'
  }
}

resource device_connection_state_events1234567890_even_longer 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  name: 'device-connection-state-events1234567890-even-longer'
  parent: sb
}

output serviceBusEndpoint string = sb.properties.serviceBusEndpoint

output serviceBusHostName string = split(replace(sb.properties.serviceBusEndpoint, 'https://', ''), ':')[0]

output name string = sb.name
