@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource mypip 'Microsoft.Network/publicIPAddresses@2025-05-01' = {
  name: take('mypip-${uniqueString(resourceGroup().id)}', 80)
  location: location
  properties: {
    publicIPAllocationMethod: 'Static'
  }
  sku: {
    name: 'Standard'
  }
  tags: {
    'aspire-resource-name': 'mypip'
  }
}

output id string = mypip.id

output name string = mypip.name

output ipAddress string = mypip.properties.ipAddress