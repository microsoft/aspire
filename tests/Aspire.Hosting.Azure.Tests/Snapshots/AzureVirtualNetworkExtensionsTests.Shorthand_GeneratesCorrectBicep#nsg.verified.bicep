@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource web_nsg 'Microsoft.Network/networkSecurityGroups@2025-05-01' = {
  name: take('web_nsg-${uniqueString(resourceGroup().id)}', 80)
  location: location
  tags: {
    'aspire-resource-name': 'web-nsg'
  }
}

resource web_nsg_allow_inbound_443_AzureLoadBalancer 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'allow-inbound-443-AzureLoadBalancer'
  properties: {
    access: 'Allow'
    destinationAddressPrefix: '*'
    destinationPortRange: '443'
    direction: 'Inbound'
    priority: 100
    protocol: 'Tcp'
    sourceAddressPrefix: 'AzureLoadBalancer'
    sourcePortRange: '*'
  }
  parent: web_nsg
}

resource web_nsg_deny_inbound_VirtualNetwork 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'deny-inbound-VirtualNetwork'
  properties: {
    access: 'Deny'
    destinationAddressPrefix: '*'
    destinationPortRange: '*'
    direction: 'Inbound'
    priority: 200
    protocol: '*'
    sourceAddressPrefix: 'VirtualNetwork'
    sourcePortRange: '*'
  }
  parent: web_nsg
}

resource web_nsg_deny_inbound_Internet 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'deny-inbound-Internet'
  properties: {
    access: 'Deny'
    destinationAddressPrefix: '*'
    destinationPortRange: '*'
    direction: 'Inbound'
    priority: 300
    protocol: '*'
    sourceAddressPrefix: 'Internet'
    sourcePortRange: '*'
  }
  parent: web_nsg
}

resource web_nsg_allow_inbound_10_0_2_0_24 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'allow-inbound-10.0.2.0-24'
  properties: {
    access: 'Allow'
    destinationAddressPrefix: '*'
    destinationPortRange: '*'
    direction: 'Inbound'
    priority: 400
    protocol: '*'
    sourceAddressPrefix: '10.0.2.0/24'
    sourcePortRange: '*'
  }
  parent: web_nsg
}

resource web_nsg_allow_outbound_2001_db8___64____0 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'allow-outbound-2001-db8---64----0'
  properties: {
    access: 'Allow'
    destinationAddressPrefix: '::/0'
    destinationPortRange: '*'
    direction: 'Outbound'
    priority: 500
    protocol: '*'
    sourceAddressPrefix: '2001:db8::/64'
    sourcePortRange: '*'
  }
  parent: web_nsg
}

output id string = web_nsg.id

output name string = web_nsg.name