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

resource container_apps_subnet 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'container-apps-subnet'
  properties: {
    addressPrefix: '10.0.0.0/23'
    delegations: [
      {
        properties: {
          serviceName: 'Microsoft.App/environments'
        }
        name: 'Microsoft.App/environments'
      }
    ]
  }
  parent: myvnet
}

output container_apps_subnet_Id string = container_apps_subnet.id

output id string = myvnet.id

output name string = myvnet.name