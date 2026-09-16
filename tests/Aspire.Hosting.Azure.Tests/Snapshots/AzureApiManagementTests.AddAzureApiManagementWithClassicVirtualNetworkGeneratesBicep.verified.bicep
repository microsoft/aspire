@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param vnet_outputs_apim_subnet_id string

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' = {
  name: take('apim${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publisherEmail: 'api-owners@example.com'
    publisherName: 'Aspire'
    virtualNetworkType: 'Internal'
    virtualNetworkConfiguration: {
      subnetResourceId: vnet_outputs_apim_subnet_id
    }
  }
  sku: {
    name: 'Premium'
    capacity: 2
  }
  identity: {
    type: 'SystemAssigned'
  }
  tags: {
    'aspire-resource-name': 'apim'
  }
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = apim.name

output id string = apim.id

output principalId string = apim.identity.principalId