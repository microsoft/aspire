@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource aks 'Microsoft.ContainerService/managedClusters@2026-01-01' = {
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
        vmSize: 'Standard_B2s'
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
    securityProfile: {
      workloadIdentity: {
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

output id string = aks.id

output name string = aks.name

output clusterFqdn string = aks.properties.fqdn

output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL

output kubeletIdentityObjectId string = aks.properties.identityProfile.kubeletidentity.objectId

output nodeResourceGroup string = aks.properties.nodeResourceGroup