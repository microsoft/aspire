@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param subnetPrefix string

resource myvnet 'Microsoft.Network/virtualNetworks@2025-05-01' = {
  name: take('myvnet-${uniqueString(resourceGroup().id)}', 64)
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.0.0.0/16'
      ]
    }
  }
  location: location
  tags: {
    'aspire-resource-name': 'myvnet'
  }
}

resource mysubnet 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'mysubnet'
  properties: {
    addressPrefix: subnetPrefix
  }
  parent: myvnet
}

output mysubnet_Id string = mysubnet.id

output id string = myvnet.id

output name string = myvnet.name