@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource private_endpoints_nsg 'Microsoft.Network/networkSecurityGroups@2025-05-01' = {
  name: take('private_endpoints_nsg-${uniqueString(resourceGroup().id)}', 80)
  location: location
  tags: {
    'aspire-resource-name': 'private-endpoints-nsg'
  }
}

resource private_endpoints_nsg_allow_inbound_443_VirtualNetwork 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'allow-inbound-443-VirtualNetwork'
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
  parent: private_endpoints_nsg
}

resource private_endpoints_nsg_deny_inbound_Internet 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'deny-inbound-Internet'
  properties: {
    access: 'Deny'
    destinationAddressPrefix: '*'
    destinationPortRange: '*'
    direction: 'Inbound'
    priority: 200
    protocol: '*'
    sourceAddressPrefix: 'Internet'
    sourcePortRange: '*'
  }
  parent: private_endpoints_nsg
}

output id string = private_endpoints_nsg.id

output name string = private_endpoints_nsg.name