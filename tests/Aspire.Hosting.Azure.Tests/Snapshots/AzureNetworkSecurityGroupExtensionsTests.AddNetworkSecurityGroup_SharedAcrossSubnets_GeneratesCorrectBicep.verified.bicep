// Resource: myvnet
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param shared_nsg_outputs_id string

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
      id: shared_nsg_outputs_id
    }
  }
  parent: myvnet
}

resource subnet2 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'subnet2'
  properties: {
    addressPrefix: '10.0.2.0/24'
    networkSecurityGroup: {
      id: shared_nsg_outputs_id
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

// Resource: shared-nsg
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource shared_nsg 'Microsoft.Network/networkSecurityGroups@2025-05-01' = {
  name: take('shared_nsg-${uniqueString(resourceGroup().id)}', 80)
  location: location
  tags: {
    'aspire-resource-name': 'shared-nsg'
  }
}

resource shared_nsg_allow_https 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
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
  parent: shared_nsg
}

output id string = shared_nsg.id

output name string = shared_nsg.name

