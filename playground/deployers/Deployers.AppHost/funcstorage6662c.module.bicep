@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource funcstorage6662c 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: take('funcstorage6662c${uniqueString(resourceGroup().id)}', 24)
  kind: 'StorageV2'
  location: location
  sku: {
    name: 'Standard_GRS'
  }
  properties: {
    accessTier: 'Hot'
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
  tags: {
    'aspire-resource-name': 'funcstorage6662c'
  }
}

output blobEndpoint string = funcstorage6662c.properties.primaryEndpoints.blob

output queueEndpoint string = funcstorage6662c.properties.primaryEndpoints.queue

output tableEndpoint string = funcstorage6662c.properties.primaryEndpoints.table

output name string = funcstorage6662c.name