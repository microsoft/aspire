// Resource: myvnet
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param nsg_one_outputs_id string

param nsg_two_outputs_id string

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
    networkSecurityGroup: {
      id: nsg_one_outputs_id
    }
  }
  parent: myvnet
}

resource subnet2 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'subnet2'
  properties: {
    addressPrefix: '10.0.2.0/24'
    networkSecurityGroup: {
      id: nsg_two_outputs_id
    }
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

// Resource: nsg-one
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource nsg_one 'Microsoft.Network/networkSecurityGroups@2025-05-01' = {
  name: take('nsg_one-${uniqueString(resourceGroup().id)}', 80)
  location: location
  tags: {
    'aspire-resource-name': 'nsg-one'
  }
}

resource nsg_one_allow_https 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
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
  parent: nsg_one
}

output id string = nsg_one.id

output name string = nsg_one.name

// Resource: nsg-two
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource nsg_two 'Microsoft.Network/networkSecurityGroups@2025-05-01' = {
  name: take('nsg_two-${uniqueString(resourceGroup().id)}', 80)
  location: location
  tags: {
    'aspire-resource-name': 'nsg-two'
  }
}

resource nsg_two_allow_https 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'allow-https'
  properties: {
    access: 'Allow'
    destinationAddressPrefix: '*'
    destinationPortRange: '443'
    direction: 'Inbound'
    priority: 100
    protocol: 'Tcp'
    sourceAddressPrefix: 'VirtualNetwork'
    sourcePortRange: '*'
  }
  parent: nsg_two
}

output id string = nsg_two.id

output name string = nsg_two.name

