@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param storage_account_name string

resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' existing = {
  name: storage_account_name
}

resource files 'Microsoft.Storage/storageAccounts/fileServices@2025-06-01' existing = {
  name: 'default'
  parent: storage
}

resource media 'Microsoft.Storage/storageAccounts/fileServices/shares@2025-06-01' existing = {
  name: 'media-share'
  parent: files
}

output blobEndpoint string = storage.properties.primaryEndpoints.blob

output dataLakeEndpoint string = storage.properties.primaryEndpoints.dfs

output queueEndpoint string = storage.properties.primaryEndpoints.queue

output tableEndpoint string = storage.properties.primaryEndpoints.table

output fileEndpoint string = storage.properties.primaryEndpoints.file

output name string = storage.name

output resourceGroupName string = resourceGroup().name

output id string = storage.id