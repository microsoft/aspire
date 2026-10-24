@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource queues 'Microsoft.Storage/storageAccounts@2026-06-01' = {
  name: take('queues${uniqueString(resourceGroup().id)}', 24)
  tags: {
    'aspire-resource-name': 'queues'
  }
  location: location
  properties: {
    azureFilesIdentityBasedAuthentication: {
      directoryServiceOptions: 'None'
      smbOAuthSettings: {
        isSmbOAuthEnabled: true
      }
    }
    isHnsEnabled: false
    minimumTlsVersion: 'TLS1_2'
    allowSharedKeyAccess: false
    accessTier: 'Hot'
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
  sku: {
    name: 'Standard_GRS'
  }
  kind: 'StorageV2'
}

resource blobs1 'Microsoft.Storage/storageAccounts/blobServices@2026-06-01' = {
  name: 'default'
  parent: queues
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices/containers@2026-06-01' = {
  name: 'plantdata'
  parent: blobs1
}

resource queues1 'Microsoft.Storage/storageAccounts/queueServices@2026-06-01' = {
  name: 'default'
  parent: queues
}

resource myqueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2026-06-01' = {
  name: 'my-queue'
  parent: queues1
}

resource files1 'Microsoft.Storage/storageAccounts/fileServices@2026-06-01' = {
  name: 'default'
  parent: queues
}

resource files 'Microsoft.Storage/storageAccounts/fileServices/shares@2026-06-01' = {
  name: 'my-file-share'
  parent: files1
}

output blobEndpoint string = queues.properties.primaryEndpoints.blob

output dataLakeEndpoint string = queues.properties.primaryEndpoints.dfs

output queueEndpoint string = queues.properties.primaryEndpoints.queue

output tableEndpoint string = queues.properties.primaryEndpoints.table

output fileEndpoint string = queues.properties.primaryEndpoints.file

output name string = queues.name

output resourceGroupName string = resourceGroup().name

output id string = queues.id