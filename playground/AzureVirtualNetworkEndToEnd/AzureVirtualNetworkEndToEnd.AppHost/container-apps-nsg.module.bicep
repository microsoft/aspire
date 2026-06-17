@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource container_apps_nsg 'Microsoft.Network/networkSecurityGroups@2025-05-01' = {
  name: take('container_apps_nsg-${uniqueString(resourceGroup().id)}', 80)
  location: location
  tags: {
    'aspire-resource-name': 'container-apps-nsg'
  }
}

resource container_apps_nsg_allow_inbound_443_AzureLoadBalancer 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
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
  parent: container_apps_nsg
}

resource container_apps_nsg_deny_inbound_VirtualNetwork 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
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
  parent: container_apps_nsg
}

resource container_apps_nsg_deny_inbound_Internet 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
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
  parent: container_apps_nsg
}

output id string = container_apps_nsg.id

output name string = container_apps_nsg.name