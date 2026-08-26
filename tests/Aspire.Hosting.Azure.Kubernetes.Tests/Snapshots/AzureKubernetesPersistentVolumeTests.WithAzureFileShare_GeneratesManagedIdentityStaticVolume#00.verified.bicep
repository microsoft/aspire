@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param aksKubeletPrincipalId_aks string

resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: take('storage${uniqueString(resourceGroup().id)}', 24)
  kind: 'StorageV2'
  location: location
  sku: {
    name: 'Standard_GRS'
  }
  properties: {
    accessTier: 'Hot'
    allowSharedKeyAccess: false
    azureFilesIdentityBasedAuthentication: {
      directoryServiceOptions: 'None'
      smbOAuthSettings: {
        isSmbOAuthEnabled: true
      }
    }
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

resource files 'Microsoft.Storage/storageAccounts/fileServices@2025-06-01' = {
  name: 'default'
  parent: storage
}

resource media_share 'Microsoft.Storage/storageAccounts/fileServices/shares@2025-06-01' = {
  name: 'media'
  parent: files
}

resource aksFilesRole_aks 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, aksKubeletPrincipalId_aks, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a235d3ee-5935-4cfb-8cc5-a3303ad5995e'))
  properties: {
    principalId: aksKubeletPrincipalId_aks
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