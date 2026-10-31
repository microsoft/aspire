@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource apim 'Microsoft.ApiManagement/service@2025-03-01-preview' existing = {
  name: 'existing-apim'
}

output gatewayUrl string = apim.properties.gatewayUrl

output name string = 'existing-apim'

output id string = apim.id
@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param privatelink_azure_api_net_outputs_name string

param vnet_outputs_private_endpoint_subnet_id string

param apim_outputs_id string

resource privatelink_azure_api_net 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: privatelink_azure_api_net_outputs_name
}

resource private_endpoint_subnet_apim_pe 'Microsoft.Network/privateEndpoints@2025-05-01' = {
  name: take('private_endpoint_subnet_apim_pe-${uniqueString(resourceGroup().id)}', 64)
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
        name: 'private-endpoint-subnet-apim-pe-connection'
      }
    ]
    subnet: {
      id: vnet_outputs_private_endpoint_subnet_id
    }
  }
  tags: {
    'aspire-resource-name': 'private-endpoint-subnet-apim-pe'
  }
}

resource private_endpoint_subnet_apim_pe_dnsgroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-05-01' = {
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
  parent: private_endpoint_subnet_apim_pe
}

output id string = private_endpoint_subnet_apim_pe.id

output name string = private_endpoint_subnet_apim_pe.name