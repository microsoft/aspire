@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource storage 'Microsoft.Storage/storageAccounts@2026-06-01' = {
  name: take('storage${uniqueString(resourceGroup().id)}', 24)
  tags: {
    'aspire-resource-name': 'storage'
  }
  location: location
  properties: {
    isHnsEnabled: true
    minimumTlsVersion: 'TLS1_2'
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Disabled'
    accessTier: 'Hot'
    networkAcls: {
      defaultAction: 'Deny'
    }
  }
  sku: {
    name: 'Standard_GRS'
  }
  kind: 'StorageV2'
}

output blobEndpoint string = storage.properties.primaryEndpoints.blob

output dataLakeEndpoint string = storage.properties.primaryEndpoints.dfs

output queueEndpoint string = storage.properties.primaryEndpoints.queue

output tableEndpoint string = storage.properties.primaryEndpoints.table

output name string = storage.name

output id string = storage.id