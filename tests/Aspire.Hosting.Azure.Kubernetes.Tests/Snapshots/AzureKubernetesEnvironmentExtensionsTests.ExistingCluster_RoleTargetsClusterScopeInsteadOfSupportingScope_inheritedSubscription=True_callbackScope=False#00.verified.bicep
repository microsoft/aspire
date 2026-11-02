targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

param cluster_group string

param cluster_name string

param supporting_group string

param supporting_subscription string

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module aks_acr 'aks-acr/aks-acr.bicep' = {
  name: 'aks-acr'
  scope: rg
  params: {
    location: location
  }
}

module aks 'aks/aks.bicep' = {
  name: 'aks'
  scope: resourceGroup(supporting_subscription, supporting_group)
  params: {
    location: location
    cluster_name: cluster_name
    cluster_group: cluster_group
  }
}

module cluster_roles 'cluster-roles/cluster-roles.bicep' = {
  name: 'cluster-roles'
  scope: resourceGroup(supporting_subscription, cluster_group)
  params: {
    location: location
    cluster_name: cluster_name
    supporting_subscription: supporting_subscription
    cluster_group: cluster_group
  }
}

module aks_kubelet_roles_aks_acr 'aks-kubelet-roles-aks-acr/aks-kubelet-roles-aks-acr.bicep' = {
  name: 'aks-kubelet-roles-aks-acr'
  scope: rg
  params: {
    location: location
    aks_acr_outputs_name: aks_acr.outputs.name
    principalId: aks.outputs.kubeletIdentityObjectId
  }
}