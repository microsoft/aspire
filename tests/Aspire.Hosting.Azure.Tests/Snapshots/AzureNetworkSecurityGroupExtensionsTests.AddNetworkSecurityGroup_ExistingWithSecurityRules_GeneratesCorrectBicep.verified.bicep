@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingNsgName string

resource web_nsg 'Microsoft.Network/networkSecurityGroups@2025-05-01' existing = {
  name: existingNsgName
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

output name string = existingNsgName