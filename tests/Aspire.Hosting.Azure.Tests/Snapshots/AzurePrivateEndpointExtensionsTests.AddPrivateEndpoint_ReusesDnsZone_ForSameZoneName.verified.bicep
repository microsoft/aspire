@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param myvnet_outputs_id string

resource privatelink_blob_core_windows_net 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.blob.core.windows.net'
  location: 'global'
  tags: {
    'aspire-resource-name': 'privatelink-blob-core-windows-net'
  }
}

resource myvnet_link 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  name: 'myvnet-link'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: myvnet_outputs_id
    }
  }
  tags: {
    'aspire-resource-name': 'privatelink-blob-core-windows-net-myvnet-link'
  }
  parent: privatelink_blob_core_windows_net
}

output id string = privatelink_blob_core_windows_net.id

output name string = 'privatelink.blob.core.windows.net'