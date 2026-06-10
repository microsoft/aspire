// Resource: myvnet
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

// Resource: web-nsg
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource web_nsg 'Microsoft.Network/networkSecurityGroups@2025-05-01' = {
  name: take('web_nsg-${uniqueString(resourceGroup().id)}', 80)
  location: location
  tags: {
    'aspire-resource-name': 'web-nsg'
  }
}

resource web_nsg_allow_https 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'allow-https'
  properties: {
    access: 'Allow'
    destinationAddressPrefix: '*'
    destinationPortRange: '443'
    direction: 'Inbound'
    priority: 100
    protocol: 'Tcp'
    sourceAddressPrefix: '*'
    sourcePortRange: '*'
  }
  parent: web_nsg
}

output id string = web_nsg.id

output name string = web_nsg.name

