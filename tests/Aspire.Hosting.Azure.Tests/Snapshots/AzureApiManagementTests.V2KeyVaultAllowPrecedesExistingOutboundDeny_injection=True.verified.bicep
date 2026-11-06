@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource subnet_nsg 'Microsoft.Network/networkSecurityGroups@2025-05-01' = {
  name: take('subnet_nsg-${uniqueString(resourceGroup().id)}', 80)
  location: location
  tags: {
    'aspire-resource-name': 'subnet-nsg'
  }
}

resource subnet_nsg_allow_inbound_443 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'allow-inbound-443'
  properties: {
    access: 'Allow'
    destinationAddressPrefix: '*'
    destinationPortRange: '443'
    direction: 'Inbound'
    priority: 100
    protocol: '*'
    sourceAddressPrefix: '*'
    sourcePortRange: '*'
  }
  parent: subnet_nsg
}

resource subnet_nsg_deny_outbound 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'deny-outbound'
  properties: {
    access: 'Deny'
    destinationAddressPrefix: '*'
    destinationPortRange: '*'
    direction: 'Outbound'
    priority: 200
    protocol: '*'
    sourceAddressPrefix: '*'
    sourcePortRange: '*'
  }
  parent: subnet_nsg
}

resource subnet_nsg_allow_apim_key_vault 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'allow-apim-key-vault'
  properties: {
    access: 'Allow'
    destinationAddressPrefix: 'AzureKeyVault'
    destinationPortRange: '443'
    direction: 'Outbound'
    priority: 101
    protocol: 'Tcp'
    sourceAddressPrefix: 'VirtualNetwork'
    sourcePortRange: '*'
  }
  parent: subnet_nsg
}

output id string = subnet_nsg.id

output name string = subnet_nsg.name