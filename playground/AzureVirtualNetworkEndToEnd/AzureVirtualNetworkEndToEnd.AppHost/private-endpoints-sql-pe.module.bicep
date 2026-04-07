@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param privatelink_database_windows_net_outputs_name string

param vnet_outputs_private_endpoints_id string

param sql_outputs_id string

resource privatelink_database_windows_net 'Microsoft.Network/privateDnsZones@2024-06-01' existing = {
  name: privatelink_database_windows_net_outputs_name
}

resource private_endpoints_sql_pe 'Microsoft.Network/privateEndpoints@2025-05-01' = {
  name: take('private_endpoints_sql_pe-${uniqueString(resourceGroup().id)}', 64)
  location: location
  properties: {
    privateLinkServiceConnections: [
      {
        properties: {
          privateLinkServiceId: sql_outputs_id
          groupIds: [
            'sqlServer'
          ]
        }
        name: 'private-endpoints-sql-pe-connection'
      }
    ]
    subnet: {
      id: vnet_outputs_private_endpoints_id
    }
  }
  tags: {
    'aspire-resource-name': 'private-endpoints-sql-pe'
  }
}

resource private_endpoints_sql_pe_dnsgroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2025-05-01' = {
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'privatelink_database_windows_net'
        properties: {
          privateDnsZoneId: privatelink_database_windows_net.id
        }
      }
    ]
  }
  parent: private_endpoints_sql_pe
}

output id string = private_endpoints_sql_pe.id

output name string = private_endpoints_sql_pe.name