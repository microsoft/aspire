@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

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

resource subnet1 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'subnet1'
  properties: {
    addressPrefix: '10.0.1.0/24'
    delegations: [
      {
        properties: {
          serviceName: 'Microsoft.App/environments'
        }
        name: 'ContainerAppsDelegation'
      }
    ]
  }
  parent: myvnet
}

resource subnet2 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'custom-subnet-name'
  properties: {
    addressPrefix: '10.0.2.0/24'
  }
  parent: myvnet
  dependsOn: [
    subnet1
  ]
}

output subnet1_Id string = subnet1.id

output subnet2_Id string = subnet2.id

output id string = myvnet.id

output name string = myvnet.name