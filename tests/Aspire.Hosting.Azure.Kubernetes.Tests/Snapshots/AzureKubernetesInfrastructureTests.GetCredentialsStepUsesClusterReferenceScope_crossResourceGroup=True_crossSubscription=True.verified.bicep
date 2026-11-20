@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-01' existing = {
  name: 'scoped-aks'
  scope: resourceGroup('00000000-0000-0000-0000-000000000002', 'annotation-rg')
}

output id string = aks.id

output name string = aks.name

output clusterFqdn string = ((aks.properties.?fqdn ?? aks.properties.?privateFQDN) ?? '')

output oidcIssuerUrl string = (aks.properties.?oidcIssuerProfile.?issuerURL ?? '')

output kubeletIdentityObjectId string = aks.properties.identityProfile.kubeletidentity.objectId

output nodeResourceGroup string = aks.properties.nodeResourceGroup