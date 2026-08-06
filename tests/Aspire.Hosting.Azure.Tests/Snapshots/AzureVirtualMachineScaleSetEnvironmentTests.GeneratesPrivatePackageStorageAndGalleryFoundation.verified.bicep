@description('The location for the resources to be deployed.')
param location string = resourceGroup().location

param resourceName string
param userPrincipalId string
param provisionPackageStorage bool
param tags object = {}

var suffix = uniqueString(resourceGroup().id)
var storageBlobDataContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

resource galleryIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('${resourceName}-gallery-${suffix}', 128)
  location: location
  tags: tags
}

resource gallery 'Microsoft.Compute/galleries@2025-03-03' = {
  name: take(replace('${resourceName}${suffix}', '-', ''), 80)
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${galleryIdentity.id}': {}
    }
  }
  properties: {
    description: 'Compute gallery for the ${resourceName} Aspire compute environment.'
  }
  tags: tags
}

resource galleryApplication 'Microsoft.Compute/galleries/applications@2025-03-03' = {
  parent: gallery
  name: 'aspire-application'
  location: location
  properties: {
    description: 'Application deployed by the ${resourceName} Aspire compute environment.'
    supportedOSType: 'Linux'
  }
}

resource packageStorage 'Microsoft.Storage/storageAccounts@2024-01-01' = if (provisionPackageStorage) {
  name: 'aspire${uniqueString(resourceGroup().id, resourceName)}'
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
  }
  tags: tags
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2024-01-01' = if (provisionPackageStorage) {
  parent: packageStorage
  name: 'default'
}

resource packageContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2024-01-01' = if (provisionPackageStorage) {
  parent: blobService
  name: 'vm-applications'
  properties: {
    publicAccess: 'None'
  }
}

resource deployerPackageContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (provisionPackageStorage) {
  name: guid(packageStorage.id, userPrincipalId, storageBlobDataContributorRoleId)
  scope: packageStorage
  properties: {
    principalId: userPrincipalId
    roleDefinitionId: storageBlobDataContributorRoleId
  }
}

resource galleryPackageReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (provisionPackageStorage) {
  name: guid(packageStorage.id, galleryIdentity.id, storageBlobDataContributorRoleId)
  scope: packageStorage
  properties: {
    principalId: galleryIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataContributorRoleId
  }
}

output packageUri string = provisionPackageStorage ? '${packageStorage!.properties.primaryEndpoints.blob}${packageContainer!.name}/aspire-application.tar.gz' : ''
output galleryName string = gallery.name
output galleryApplicationName string = galleryApplication.name
