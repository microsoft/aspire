@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource sql_nsg 'Microsoft.Network/networkSecurityGroups@2025-05-01' = {
  name: take('sql_nsg-${uniqueString(resourceGroup().id)}', 80)
  location: location
  tags: {
    'aspire-resource-name': 'sql-nsg'
  }
}

resource sql_nsg_allow_outbound_443_AzureActiveDirectory 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'allow-outbound-443-AzureActiveDirectory'
  properties: {
    access: 'Allow'
    destinationAddressPrefix: 'AzureActiveDirectory'
    destinationPortRange: '443'
    direction: 'Outbound'
    priority: 100
    protocol: 'Tcp'
    sourceAddressPrefix: '*'
    sourcePortRange: '*'
  }
  parent: sql_nsg
}

resource sql_nsg_allow_outbound_443_Sql 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'allow-outbound-443-Sql'
  properties: {
    access: 'Allow'
    destinationAddressPrefix: 'Sql'
    destinationPortRange: '443'
    direction: 'Outbound'
    priority: 200
    protocol: 'Tcp'
    sourceAddressPrefix: '*'
    sourcePortRange: '*'
  }
  parent: sql_nsg
}

output id string = sql_nsg.id

output name string = sql_nsg.name