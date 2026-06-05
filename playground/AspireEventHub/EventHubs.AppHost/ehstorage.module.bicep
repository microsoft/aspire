@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource ehstorage 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: take('ehstorage${uniqueString(resourceGroup().id)}', 24)
  kind: 'StorageV2'
  location: location
  sku: {
    name: 'Standard_GRS'
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
    'aspire-resource-name': 'ehstorage'
  }
}

output blobEndpoint string = ehstorage.properties.primaryEndpoints.blob

output dataLakeEndpoint string = ehstorage.properties.primaryEndpoints.dfs

output queueEndpoint string = ehstorage.properties.primaryEndpoints.queue

output tableEndpoint string = ehstorage.properties.primaryEndpoints.table

output name string = ehstorage.name

output id string = ehstorage.id