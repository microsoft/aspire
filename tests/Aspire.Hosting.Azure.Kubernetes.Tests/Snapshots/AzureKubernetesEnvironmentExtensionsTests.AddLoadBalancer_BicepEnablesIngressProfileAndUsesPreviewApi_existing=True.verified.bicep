@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param cluster_name string

param vnet_outputs_name string

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-02-preview' existing = {
  name: cluster_name
}

resource vnet 'Microsoft.Network/virtualNetworks@2025-05-01' existing = {
  name: vnet_outputs_name
}

resource vnet_alb_existing 'Microsoft.Network/virtualNetworks/subnets@2025-05-01' existing = {
  name: 'alb'
  parent: vnet
}

resource albSubnetJoin_lb 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vnet_alb_existing.id, aks.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4d97b98b-1d4f-4787-a291-c67834d212e7'))
  properties: {
    principalId: aks.properties.ingressProfile.applicationLoadBalancer.identity.objectId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4d97b98b-1d4f-4787-a291-c67834d212e7')
    principalType: 'ServicePrincipal'
  }
  scope: vnet_alb_existing
}

output id string = aks.id

output name string = aks.name

output clusterFqdn string = ((aks.properties.?fqdn ?? aks.properties.?privateFQDN) ?? '')

output oidcIssuerUrl string = (aks.properties.?oidcIssuerProfile.?issuerURL ?? '')

output kubeletIdentityObjectId string = aks.properties.identityProfile.kubeletidentity.objectId

output nodeResourceGroup string = aks.properties.nodeResourceGroup