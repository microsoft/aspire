@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param privatelink_services_ai_azure_com_outputs_name string

param privatelink_openai_azure_com_outputs_name string

param privatelink_cognitiveservices_azure_com_outputs_name string

param myvnet_outputs_pesubnet_id string

param foundry_outputs_id string

resource privatelink_services_ai_azure_com 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: privatelink_services_ai_azure_com_outputs_name
}

resource privatelink_openai_azure_com 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: privatelink_openai_azure_com_outputs_name
}

resource privatelink_cognitiveservices_azure_com 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: privatelink_cognitiveservices_azure_com_outputs_name
}

resource pesubnet_foundry_pe 'Microsoft.Network/privateEndpoints@2025-05-01' = {
  name: take('pesubnet_foundry_pe-${uniqueString(resourceGroup().id)}', 64)
  location: location
  properties: {
    privateLinkServiceConnections: [
      {
        properties: {
          privateLinkServiceId: foundry_outputs_id
          groupIds: [
            'account'
          ]
        }
        name: 'pesubnet-foundry-pe-connection'
      }
    ]
    subnet: {
      id: myvnet_outputs_pesubnet_id
    }
  }
  tags: {
    'aspire-resource-name': 'pesubnet-foundry-pe'
  }
}

resource pesubnet_foundry_pe_dnsgroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-05-01' = {
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'privatelink_services_ai_azure_com'
        properties: {
          privateDnsZoneId: privatelink_services_ai_azure_com.id
        }
      }
      {
        name: 'privatelink_openai_azure_com'
        properties: {
          privateDnsZoneId: privatelink_openai_azure_com.id
        }
      }
      {
        name: 'privatelink_cognitiveservices_azure_com'
        properties: {
          privateDnsZoneId: privatelink_cognitiveservices_azure_com.id
        }
      }
    ]
  }
  parent: pesubnet_foundry_pe
}

output id string = pesubnet_foundry_pe.id

output name string = pesubnet_foundry_pe.name