// Resource: vnet
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource vnet 'Microsoft.Network/virtualNetworks@2025-05-01' = {
  name: take('vnet-${uniqueString(resourceGroup().id)}', 64)
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.0.0.0/16'
      ]
    }
  }
  location: location
  tags: {
    'aspire-resource-name': 'vnet'
  }
}

resource private_endpoint_subnet_1 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'private-endpoint-subnet-1'
  properties: {
    addressPrefix: '10.0.1.0/24'
  }
  parent: vnet
}

resource private_endpoint_subnet_2 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' = {
  name: 'private-endpoint-subnet-2'
  properties: {
    addressPrefix: '10.0.2.0/24'
  }
  parent: vnet
  dependsOn: [
    private_endpoint_subnet_1
  ]
}

output private_endpoint_subnet_1_Id string = private_endpoint_subnet_1.id

output private_endpoint_subnet_2_Id string = private_endpoint_subnet_2.id

output id string = vnet.id

output name string = vnet.name

// Resource: apim
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' = {
  name: take('apim${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    publisherEmail: 'api-owners@example.com'
    publisherName: 'Aspire'
    virtualNetworkType: 'None'
  }
  sku: {
    name: 'Developer'
    capacity: 1
  }
  identity: {
    type: 'SystemAssigned'
  }
  tags: {
    'aspire-resource-name': 'apim'
  }
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = apim.name

output id string = apim.id

output principalId string = apim.identity.principalId

// Resource: privatelink-azure-api-net
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param vnet_outputs_id string

resource privatelink_azure_api_net 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.azure-api.net'
  location: 'global'
  tags: {
    'aspire-resource-name': 'privatelink-azure-api-net'
  }
}

resource vnet_link 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  name: 'vnet-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: vnet_outputs_id
    }
  }
  tags: {
    'aspire-resource-name': 'privatelink-azure-api-net-vnet-link'
  }
  parent: privatelink_azure_api_net
}

output id string = privatelink_azure_api_net.id

output name string = 'privatelink.azure-api.net'

// Resource: private-endpoint-subnet-1-apim-pe
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param privatelink_azure_api_net_outputs_name string

param vnet_outputs_private_endpoint_subnet_1_id string

param apim_outputs_id string

resource privatelink_azure_api_net 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: privatelink_azure_api_net_outputs_name
}

resource private_endpoint_subnet_1_apim_pe 'Microsoft.Network/privateEndpoints@2025-05-01' = {
  name: take('private_endpoint_subnet_1_apim_pe-${uniqueString(resourceGroup().id)}', 64)
  location: location
  properties: {
    privateLinkServiceConnections: [
      {
        properties: {
          privateLinkServiceId: apim_outputs_id
          groupIds: [
            'Gateway'
          ]
        }
        name: 'private-endpoint-subnet-1-apim-pe-connection'
      }
    ]
    subnet: {
      id: vnet_outputs_private_endpoint_subnet_1_id
    }
  }
  tags: {
    'aspire-resource-name': 'private-endpoint-subnet-1-apim-pe'
  }
}

resource private_endpoint_subnet_1_apim_pe_dnsgroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-05-01' = {
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'privatelink_azure_api_net'
        properties: {
          privateDnsZoneId: privatelink_azure_api_net.id
        }
      }
    ]
  }
  parent: private_endpoint_subnet_1_apim_pe
}

output id string = private_endpoint_subnet_1_apim_pe.id

output name string = private_endpoint_subnet_1_apim_pe.name

// Resource: private-endpoint-subnet-2-apim-pe
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param privatelink_azure_api_net_outputs_name string

param vnet_outputs_private_endpoint_subnet_2_id string

param apim_outputs_id string

resource privatelink_azure_api_net 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: privatelink_azure_api_net_outputs_name
}

resource private_endpoint_subnet_2_apim_pe 'Microsoft.Network/privateEndpoints@2025-05-01' = {
  name: take('private_endpoint_subnet_2_apim_pe-${uniqueString(resourceGroup().id)}', 64)
  location: location
  properties: {
    privateLinkServiceConnections: [
      {
        properties: {
          privateLinkServiceId: apim_outputs_id
          groupIds: [
            'Gateway'
          ]
        }
        name: 'private-endpoint-subnet-2-apim-pe-connection'
      }
    ]
    subnet: {
      id: vnet_outputs_private_endpoint_subnet_2_id
    }
  }
  tags: {
    'aspire-resource-name': 'private-endpoint-subnet-2-apim-pe'
  }
}

resource private_endpoint_subnet_2_apim_pe_dnsgroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-05-01' = {
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'privatelink_azure_api_net'
        properties: {
          privateDnsZoneId: privatelink_azure_api_net.id
        }
      }
    ]
  }
  parent: private_endpoint_subnet_2_apim_pe
}

output id string = private_endpoint_subnet_2_apim_pe.id

output name string = private_endpoint_subnet_2_apim_pe.name