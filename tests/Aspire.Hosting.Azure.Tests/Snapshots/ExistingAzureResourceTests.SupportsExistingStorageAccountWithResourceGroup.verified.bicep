@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' existing = {
  name: existingResourceName
}

output blobEndpoint string = storage.properties.primaryEndpoints.blob

output dataLakeEndpoint string = storage.properties.primaryEndpoints.dfs

output queueEndpoint string = storage.properties.primaryEndpoints.queue

output tableEndpoint string = storage.properties.primaryEndpoints.table

output name string = storage.name

output id string = storage.id