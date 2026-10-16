@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param cluster_name string

param acrName string

param identityName_myapi string

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-01' existing = {
  name: cluster_name
}

resource acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: acrName
}

resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, aks.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: aks.properties.identityProfile.kubeletidentity.objectId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: acr
}

resource identity_myapi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: identityName_myapi
}

resource fedcred_myapi 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  name: 'myapi-fedcred'
  properties: {
    audiences: [
      'api://AzureADTokenExchange'
    ]
    issuer: aks.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:default:myapi-sa'
  }
  parent: identity_myapi
}

output id string = aks.id

output name string = aks.name

output clusterFqdn string = aks.properties.fqdn

output oidcIssuerUrl string = (aks.properties.?oidcIssuerProfile.?issuerURL ?? '')

output kubeletIdentityObjectId string = aks.properties.identityProfile.kubeletidentity.objectId

output nodeResourceGroup string = aks.properties.nodeResourceGroup