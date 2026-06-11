@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param mypip_outputs_id string

resource mynat 'Microsoft.Network/natGateways@2025-05-01' = {
  name: take('mynat${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publicIpAddresses: [
      {
        id: mypip_outputs_id
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