@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource mynat_pip 'Microsoft.Network/publicIPAddresses@2025-05-01' = {
  name: take('mynat_pip-${uniqueString(resourceGroup().id)}', 80)
  location: location
  properties: {
    publicIPAllocationMethod: 'Static'
  }
  sku: {
    name: 'Standard'
  }
  tags: {
    'aspire-resource-name': 'mynat'
  }
}

resource mynat 'Microsoft.Network/natGateways@2025-05-01' = {
  name: take('mynat${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publicIpAddresses: [
      {
        id: mynat_pip.id
      }
    ]
  }
  sku: {
    name: 'Standard'
  }
  tags: {
    'aspire-resource-name': 'mynat'
  }
}

output id string = mynat.id

output name string = mynat.name