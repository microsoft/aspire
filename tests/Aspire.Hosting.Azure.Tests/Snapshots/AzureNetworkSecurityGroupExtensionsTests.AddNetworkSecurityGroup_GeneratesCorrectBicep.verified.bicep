@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param web_nsg_outputs_id string

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

resource web_subnet 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'web-subnet'
  properties: {
    addressPrefix: '10.0.1.0/24'
    networkSecurityGroup: {
      id: web_nsg_outputs_id
    }
  }
  parent: myvnet
}

output web_subnet_Id string = web_subnet.id

output id string = myvnet.id

output name string = myvnet.name