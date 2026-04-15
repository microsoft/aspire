@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource aks 'Microsoft.ContainerService/managedClusters@2026-01-01' = {
  name: 'aks'
  location: location
  tags: {
    'aspire-resource-name': 'aks'
  }
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: 'Base'
    tier: 'Free'
  }
  properties: {
    kubernetesVersion: '1.30'
    dnsPrefix: 'aks-dns'
    agentPoolProfiles: [
      {
        name: 'system'
        vmSize: 'Standard_D4s_v5'
        minCount: 1
        maxCount: 3
        count: 1
        enableAutoScaling: true
        mode: 'System'
        osType: 'Linux'
      }
    ]
    oidcIssuerProfile: {
      enabled: true
    }
    securityProfile: {
      workloadIdentity: {
        enabled: true
      }
    }
  }
}

output id string = aks.id
output name string = aks.name
output clusterFqdn string = aks.properties.fqdn
output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL
output kubeletIdentityObjectId string = aks.properties.identityProfile.kubeletidentity.objectId
output nodeResourceGroup string = aks.properties.nodeResourceGroup
