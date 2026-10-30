@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param aks_control_plane_identity_outputs_principalname string

param aks_kubelet_identity_outputs_principalname string

param subnetId string

param vnet_outputs_name string

resource aks_control_plane_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: aks_control_plane_identity_outputs_principalname
}

resource aks_kubelet_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: aks_kubelet_identity_outputs_principalname
}

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-02-preview' = {
  name: take('aks-${uniqueString(resourceGroup().id)}', 63)
  tags: {
    'aspire-resource-name': 'aks'
  }
  location: location
  properties: {
    dnsPrefix: 'aks-dns'
    agentPoolProfiles: [
      {
        count: 1
        vmSize: 'Standard_D2s_v5'
        vnetSubnetID: subnetId
        osType: 'Linux'
        maxCount: 3
        minCount: 1
        enableAutoScaling: true
        mode: 'System'
        name: 'system'
      }
    ]
    oidcIssuerProfile: {
      enabled: true
    }
    networkProfile: {
      networkPlugin: 'azure'
    }
    identityProfile: {
      kubeletidentity: {
        resourceId: aks_kubelet_identity.id
        clientId: aks_kubelet_identity.properties.clientId
        objectId: aks_kubelet_identity.properties.principalId
      }
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
    tier: 'Standard'
  }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${aks_control_plane_identity.id}': { }
    }
  }
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