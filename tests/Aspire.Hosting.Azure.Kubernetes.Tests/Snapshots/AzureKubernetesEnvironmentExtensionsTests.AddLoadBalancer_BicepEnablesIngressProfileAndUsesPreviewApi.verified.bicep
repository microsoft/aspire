@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param subnetId string

param acrName string

param vnet_outputs_name string

resource aks 'Microsoft.ContainerService/managedClusters@2025-09-02-preview' = {
  name: take('aks-${uniqueString(resourceGroup().id)}', 63)
  tags: {
    'aspire-resource-name': 'aks'
  }
  location: location
  properties: {
    dnsPrefix: 'aks-dns'
    agentPoolProfiles: [
      {
        name: 'system'
        count: 1
        vmSize: 'Standard_D2s_v5'
        vnetSubnetID: subnetId
        osType: 'Linux'
        maxCount: 3
        minCount: 1
        enableAutoScaling: true
        mode: 'System'
      }
    ]
    oidcIssuerProfile: {
      enabled: true
    }
    networkProfile: {
      networkPlugin: 'azure'
    }
    securityProfile: {
      workloadIdentity: {
        enabled: true
      }
    }
    ingressProfile: {
      gatewayAPI: {
        installation: 'Standard'
      }
      applicationLoadBalancer: {
        enabled: true
      }
    }
  }
  sku: {
    name: 'Base'
    tier: 'Free'
  }
  identity: {
    type: 'SystemAssigned'
  }
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

output clusterFqdn string = aks.properties.fqdn

output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL

output kubeletIdentityObjectId string = aks.properties.identityProfile.kubeletidentity.objectId

output nodeResourceGroup string = aks.properties.nodeResourceGroup