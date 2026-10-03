@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param acrName string

param identityName_app__media_volume string

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
        vmSize: 'Standard_D2s_v5'
        osType: 'Linux'
        maxCount: 3
        minCount: 1
        enableAutoScaling: true
        mode: 'System'
      }
      {
        name: 'workload'
        count: 1
        vmSize: 'Standard_D2s_v5'
        osType: 'Linux'
        maxCount: 3
        minCount: 1
        enableAutoScaling: true
        mode: 'User'
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
    storageProfile: {
      fileCSIDriver: {
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

resource identity_app__media_volume 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: identityName_app__media_volume
}

resource fedcred_app__media_volume 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  name: 'app-fedcred'
  properties: {
    audiences: [
      'api://AzureADTokenExchange'
    ]
    issuer: aks.properties.oidcIssuerProfile.issuerURL
    subject: 'system:serviceaccount:default:app-sa'
  }
  parent: identity_app__media_volume
}

output id string = aks.id

output name string = aks.name

output clusterFqdn string = aks.properties.fqdn

output oidcIssuerUrl string = aks.properties.oidcIssuerProfile.issuerURL

output kubeletIdentityObjectId string = aks.properties.identityProfile.kubeletidentity.objectId

output nodeResourceGroup string = aks.properties.nodeResourceGroup