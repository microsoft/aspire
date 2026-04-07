// Resource: mypip
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource mypip 'Microsoft.Network/publicIPAddresses@2025-05-01' = {
  name: take('mypip-${uniqueString(resourceGroup().id)}', 80)
  location: location
  properties: {
    publicIPAllocationMethod: 'Static'
  }
  sku: {
    name: 'Standard'
  }
  tags: {
    'aspire-resource-name': 'mypip'
  }
}

output id string = mypip.id

output name string = mypip.name

output ipAddress string = mypip.properties.ipAddress

// Resource: web-nsg
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param mypip_outputs_ipaddress string

resource web_nsg 'Microsoft.Network/networkSecurityGroups@2025-05-01' = {
  name: take('web_nsg-${uniqueString(resourceGroup().id)}', 80)
  location: location
  tags: {
    'aspire-resource-name': 'web-nsg'
  }
}

resource web_nsg_allow_from_pip 'Microsoft.Network/networkSecurityGroups/securityRules@2025-05-01' = {
  name: 'allow-from-pip'
  properties: {
    access: 'Allow'
    destinationAddressPrefix: '*'
    destinationPortRange: '443'
    direction: 'Inbound'
    priority: 100
    protocol: 'Tcp'
    sourceAddressPrefix: mypip_outputs_ipaddress
    sourcePortRange: '*'
  }
  parent: web_nsg
}

output id string = web_nsg.id

output name string = web_nsg.name

