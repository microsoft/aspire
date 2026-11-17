@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param aks_control_plane_identity_outputs_principalname string

param aks_kubelet_identity_outputs_principalname string

resource aks_control_plane_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: aks_control_plane_identity_outputs_principalname
}

resource aks_kubelet_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: aks_kubelet_identity_outputs_principalname
}

resource aks 'Microsoft.ContainerService/managedClusters@2026-07-01' = {
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
        osType: 'Linux'
        maxCount: 3
        minCount: 1
        enableAutoScaling: true
        mode: 'System'
        name: 'system'
      }
      {
        count: 1
        vmSize: 'Standard_D2s_v5'
        osType: 'Linux'
        maxCount: 3
        minCount: 1
        enableAutoScaling: true
        mode: 'User'
        name: 'workers'
      }
      {
        count: 1
        vmSize: 'Standard_D2s_v5'
        osType: 'Linux'
        maxCount: 3
        minCount: 1
        enableAutoScaling: true
        mode: 'User'
        name: 'defaults'
      }
    ]
    oidcIssuerProfile: {
      enabled: true
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

output id string = aks.id

output name string = aks.name

output clusterFqdn string = aks.properties.fqdn

output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL

output kubeletIdentityObjectId string = aks.properties.identityProfile.kubeletidentity.objectId

output nodeResourceGroup string = aks.properties.nodeResourceGroup