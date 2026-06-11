@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: take('storage${uniqueString(resourceGroup().id)}', 24)
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
    'aspire-resource-name': 'storage'
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2024-01-01' = {
  name: 'default'
  parent: storage
}

resource mycontainer1 'Microsoft.Storage/storageAccounts/blobServices/containers@2024-01-01' = {
  name: 'test-container-1'
  parent: blobs
}

resource mycontainer2 'Microsoft.Storage/storageAccounts/blobServices/containers@2024-01-01' = {
  name: 'test-container-2'
  parent: blobs
}

resource queues 'Microsoft.Storage/storageAccounts/queueServices@2024-01-01' = {
  name: 'default'
  parent: storage
}

resource myqueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2024-01-01' = {
  name: 'my-queue'
  parent: queues
}

output blobEndpoint string = storage.properties.primaryEndpoints.blob

output dataLakeEndpoint string = storage.properties.primaryEndpoints.dfs

output queueEndpoint string = storage.properties.primaryEndpoints.queue

output tableEndpoint string = storage.properties.primaryEndpoints.table

output name string = storage.name

output id string = storage.id