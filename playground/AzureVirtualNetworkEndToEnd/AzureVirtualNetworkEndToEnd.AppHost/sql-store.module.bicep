@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource sql_store 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: take('sqlstore${uniqueString(resourceGroup().id)}', 24)
  kind: 'StorageV2'
  location: location
  sku: {
    name: 'Standard_GRS'
  }
  properties: {
    accessTier: 'Hot'
    allowSharedKeyAccess: true
    isHnsEnabled: false
    minimumTlsVersion: 'TLS1_2'
    networkAcls: {
      defaultAction: 'Deny'
    }
    publicNetworkAccess: 'Disabled'
  }
  tags: {
    'aspire-resource-name': 'sql-store'
  }
}

output blobEndpoint string = sql_store.properties.primaryEndpoints.blob

output dataLakeEndpoint string = sql_store.properties.primaryEndpoints.dfs

output queueEndpoint string = sql_store.properties.primaryEndpoints.queue

output tableEndpoint string = sql_store.properties.primaryEndpoints.table

output name string = sql_store.name

output id string = sql_store.id