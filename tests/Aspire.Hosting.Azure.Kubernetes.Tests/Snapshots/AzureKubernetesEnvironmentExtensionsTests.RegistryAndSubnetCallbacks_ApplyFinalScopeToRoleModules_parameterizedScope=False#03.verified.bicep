@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource network 'Microsoft.Network/virtualNetworks@2025-05-01' existing = {
  name: 'existingvnet'
}

resource subnet 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'subnet'
  properties: {
    addressPrefix: '10.0.0.0/22'
  }
  parent: network
}

output subnet_Id string = subnet.id

output id string = network.id

output name string = 'existingvnet'