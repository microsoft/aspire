@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource aks 'Microsoft.ContainerService/managedClusters@2025-03-01' = {
  name: take('aks-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    agentPoolProfiles: [
      {
        name: 'system'
        count: 1
        vmSize: 'Standard_D4s_v5'
        osType: 'Linux'
        maxCount: 3
        minCount: 1
        enableAutoScaling: true
        mode: 'System'
      }
    ]
    dnsPrefix: 'aks-dns'
    kubernetesVersion: '1.30'
    oidcIssuerProfile: {
      enabled: true
    }
    securityProfile: {
      workloadIdentity: {
        enabled: true
      }
    }
  }
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: 'Base'
    tier: 'Free'
  }
  tags: {
    'aspire-resource-name': 'aks'
  }
}

output id string = aks.id

output name string = aks.name

output clusterFqdn string = aks.properties.fqdn

output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL

output kubeletIdentityObjectId string = aks.properties.identityProfile.kubeletidentity.objectId

output nodeResourceGroup string = aks.properties.nodeResourceGroup