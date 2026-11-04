@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param cluster_name string

param identityName_myapi string

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-01' existing = {
  name: cluster_name
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

output clusterFqdn string = ((aks.properties.?fqdn ?? aks.properties.?privateFQDN) ?? '')

output oidcIssuerUrl string = (aks.properties.?oidcIssuerProfile.?issuerURL ?? '')

output kubeletIdentityObjectId string = aks.properties.identityProfile.kubeletidentity.objectId

output nodeResourceGroup string = aks.properties.nodeResourceGroup