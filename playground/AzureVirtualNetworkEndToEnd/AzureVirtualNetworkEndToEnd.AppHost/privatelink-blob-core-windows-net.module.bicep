@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param vnet_outputs_id string

resource privatelink_blob_core_windows_net 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.blob.core.windows.net'
  location: 'global'
  tags: {
    'aspire-resource-name': 'privatelink-blob-core-windows-net'
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
    'aspire-resource-name': 'privatelink-blob-core-windows-net-vnet-link'
  }
  parent: privatelink_blob_core_windows_net
}

output id string = privatelink_blob_core_windows_net.id

output name string = 'privatelink.blob.core.windows.net'