@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param privatelink_blob_core_windows_net_outputs_name string

param myvnet_outputs_pesubnet_id string

param storage_outputs_id string

resource privatelink_blob_core_windows_net 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: privatelink_blob_core_windows_net_outputs_name
}

resource pesubnet_blobs_pe 'Microsoft.Network/privateEndpoints@2025-05-01' = {
  name: take('pesubnet_blobs_pe-${uniqueString(resourceGroup().id)}', 64)
  location: location
  properties: {
    privateLinkServiceConnections: [
      {
        properties: {
          privateLinkServiceId: storage_outputs_id
          groupIds: [
            'blob'
          ]
        }
        name: 'pesubnet-blobs-pe-connection'
      }
    ]
    subnet: {
      id: myvnet_outputs_pesubnet_id
    }
  }
  tags: {
    'aspire-resource-name': 'pesubnet-blobs-pe'
  }
}

resource pesubnet_blobs_pe_dnsgroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-05-01' = {
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
  parent: pesubnet_blobs_pe
}

output id string = pesubnet_blobs_pe.id

output name string = pesubnet_blobs_pe.name