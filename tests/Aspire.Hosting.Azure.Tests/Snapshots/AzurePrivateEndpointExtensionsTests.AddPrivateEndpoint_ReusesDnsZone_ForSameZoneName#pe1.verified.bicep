@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param privatelink_blob_core_windows_net_outputs_name string

param myvnet_outputs_pesubnet_id string

param storage1_outputs_id string

resource privatelink_blob_core_windows_net 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: privatelink_blob_core_windows_net_outputs_name
}

resource pesubnet_blobs1_pe 'Microsoft.Network/privateEndpoints@2025-05-01' = {
  name: take('pesubnet_blobs1_pe-${uniqueString(resourceGroup().id)}', 64)
  location: location
  properties: {
    privateLinkServiceConnections: [
      {
        properties: {
          privateLinkServiceId: storage1_outputs_id
          groupIds: [
            'blob'
          ]
        }
        name: 'pesubnet-blobs1-pe-connection'
      }
    ]
    subnet: {
      id: myvnet_outputs_pesubnet_id
    }
  }
  tags: {
    'aspire-resource-name': 'pesubnet-blobs1-pe'
  }
}

resource pesubnet_blobs1_pe_dnsgroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-05-01' = {
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'privatelink_blob_core_windows_net'
        properties: {
          privateDnsZoneId: privatelink_blob_core_windows_net.id
        }
      }
    ]
  }
  parent: pesubnet_blobs1_pe
}

output id string = pesubnet_blobs1_pe.id

output name string = pesubnet_blobs1_pe.name