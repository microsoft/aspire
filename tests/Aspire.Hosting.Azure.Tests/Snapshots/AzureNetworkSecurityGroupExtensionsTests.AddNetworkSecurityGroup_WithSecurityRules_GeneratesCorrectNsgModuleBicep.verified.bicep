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

resource web_nsg_deny_all_inbound 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'deny-all-inbound'
  properties: {
    access: 'Deny'
    destinationAddressPrefix: '*'
    destinationPortRange: '*'
    direction: 'Inbound'
    priority: 4096
    protocol: '*'
    sourceAddressPrefix: '*'
    sourcePortRange: '*'
  }
  parent: web_nsg
}

output id string = web_nsg.id

output name string = web_nsg.name