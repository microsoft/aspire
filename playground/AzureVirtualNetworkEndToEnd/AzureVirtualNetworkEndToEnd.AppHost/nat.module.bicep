@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource nat_pip 'Microsoft.Network/publicIPAddresses@2025-05-01' = {
  name: take('nat_pip-${uniqueString(resourceGroup().id)}', 80)
  location: location
  properties: {
    publicIPAllocationMethod: 'Static'
  }
  sku: {
    name: 'Standard'
  }
  tags: {
    'aspire-resource-name': 'nat'
  }
}

resource nat 'Microsoft.Network/natGateways@2025-05-01' = {
  name: take('nat${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publicIpAddresses: [
      {
        id: nat_pip.id
      }
    ]
  }
  sku: {
    name: 'Standard'
  }
  tags: {
    'aspire-resource-name': 'nat'
  }
}

output id string = nat.id

output name string = nat.name