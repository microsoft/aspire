@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param azureFilesIdentityPrincipalId_media_volume string

resource storage 'Microsoft.Storage/storageAccounts@2026-06-01' = {
  name: take('storage${uniqueString(resourceGroup().id)}', 24)
  tags: {
    'aspire-resource-name': 'storage'
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

resource files 'Microsoft.Storage/storageAccounts/fileServices@2026-06-01' = {
  name: 'default'
  parent: storage
}

resource media_share 'Microsoft.Storage/storageAccounts/fileServices/shares@2026-06-01' = {
  name: 'media'
  parent: files
}

resource azureFilesRole_media_volume 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, azureFilesIdentityPrincipalId_media_volume, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a235d3ee-5935-4cfb-8cc5-a3303ad5995e'))
  properties: {
    principalId: azureFilesIdentityPrincipalId_media_volume
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a235d3ee-5935-4cfb-8cc5-a3303ad5995e')
    principalType: 'ServicePrincipal'
  }
  scope: storage
}

output blobEndpoint string = storage.properties.primaryEndpoints.blob

output dataLakeEndpoint string = storage.properties.primaryEndpoints.dfs

output queueEndpoint string = storage.properties.primaryEndpoints.queue

output tableEndpoint string = storage.properties.primaryEndpoints.table

output fileEndpoint string = storage.properties.primaryEndpoints.file

output name string = storage.name

output resourceGroupName string = resourceGroup().name

output id string = storage.id