@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sku string = 'Standard'

resource sb 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: toLower(take('sb${uniqueString(resourceGroup().id)}', 24))
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

output serviceBusEndpoint string = sb.properties.serviceBusEndpoint

output serviceBusHostName string = split(replace(sb.properties.serviceBusEndpoint, 'https://', ''), ':')[0]

output name string = sb.name
