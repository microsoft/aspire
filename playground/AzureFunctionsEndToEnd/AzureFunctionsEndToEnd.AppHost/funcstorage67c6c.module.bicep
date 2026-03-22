@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource funcstorage67c6c 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: take('funcstorage67c6c${uniqueString(resourceGroup().id)}', 24)
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
    'aspire-resource-name': 'funcstorage67c6c'
  }
}

output blobEndpoint string = funcstorage67c6c.properties.primaryEndpoints.blob

output dataLakeEndpoint string = funcstorage67c6c.properties.primaryEndpoints.dfs

output queueEndpoint string = funcstorage67c6c.properties.primaryEndpoints.queue

output tableEndpoint string = funcstorage67c6c.properties.primaryEndpoints.table

output name string = funcstorage67c6c.name

output id string = funcstorage67c6c.id