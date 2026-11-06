@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param cluster_name string

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-01' existing = {
  name: cluster_name
}

output id string = aks.id

output name string = aks.name

output clusterFqdn string = ((aks.properties.?fqdn ?? aks.properties.?privateFQDN) ?? '')

output oidcIssuerUrl string = (aks.properties.?oidcIssuerProfile.?issuerURL ?? '')

output kubeletIdentityObjectId string = aks.properties.identityProfile.kubeletidentity.objectId

output nodeResourceGroup string = aks.properties.nodeResourceGroup