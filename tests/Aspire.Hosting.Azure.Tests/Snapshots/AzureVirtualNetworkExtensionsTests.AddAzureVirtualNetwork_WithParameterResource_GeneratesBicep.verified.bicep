@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param vnetPrefix string

resource myvnet 'Microsoft.Network/virtualNetworks@2025-05-01' = {
  name: take('myvnet-${uniqueString(resourceGroup().id)}', 64)
  properties: {
    addressSpace: {
      addressPrefixes: [
        vnetPrefix
      ]
    }
  }
  location: location
  tags: {
    'aspire-resource-name': 'myvnet'
  }
}

output id string = myvnet.id

output name string = myvnet.name