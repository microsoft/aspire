@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param storagesku string

resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: take('storage${uniqueString(resourceGroup().id)}', 24)
  kind: 'StorageV2'
  location: location
  sku: {
    name: storagesku
  }
  properties: {
    accessTier: 'Hot'
    allowSharedKeyAccess: false
    isHnsEnabled: false
    minimumTlsVersion: 'TLS1_2'
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
  tags: {
    'aspire-resource-name': 'storage'
  }
}

output blobEndpoint string = storage.properties.primaryEndpoints.blob

output dataLakeEndpoint string = storage.properties.primaryEndpoints.dfs

output queueEndpoint string = storage.properties.primaryEndpoints.queue

output tableEndpoint string = storage.properties.primaryEndpoints.table

output fileEndpoint string = storage.properties.primaryEndpoints.file

output name string = storage.name

output resourceGroupName string = resourceGroup().name

output id string = storage.id