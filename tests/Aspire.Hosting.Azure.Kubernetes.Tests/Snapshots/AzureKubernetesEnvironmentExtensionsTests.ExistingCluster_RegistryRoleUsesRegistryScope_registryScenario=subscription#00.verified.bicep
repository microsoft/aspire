targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

param registry_group string

param registry_subscription string

param registry_name string

param cluster_group string

param cluster_subscription string

param cluster_name string

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module registry 'registry/registry.bicep' = {
  name: 'registry'
  scope: resourceGroup(registry_subscription, registry_group)
  params: {
    location: location
    registry_name: registry_name
  }
}

module aks 'aks/aks.bicep' = {
  name: 'aks'
  scope: rg
  params: {
    location: location
    cluster_name: cluster_name
    cluster_subscription: cluster_subscription
    cluster_group: cluster_group
  }
}

module aks_kubelet_roles_registry 'aks-kubelet-roles-registry/aks-kubelet-roles-registry.bicep' = {
  name: 'aks-kubelet-roles-registry'
  scope: resourceGroup(registry_subscription, registry_group)
  params: {
    location: location
    registry_name: registry_name
    clusterId: aks.outputs.id
    principalId: aks.outputs.kubeletIdentityObjectId
  }
}