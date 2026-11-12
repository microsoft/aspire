targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

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
  scope: resourceGroup('00000000-0000-0000-0000-000000000001', 'cluster-rg')
  params: {
    location: location
    aks_control_plane_identity_outputs_principalname: aks_control_plane_identity.outputs.principalName
    aks_kubelet_identity_outputs_principalname: aks_kubelet_identity.outputs.principalName
  }
  dependsOn: [
    aks_control_plane_identity_roles_aks_kubelet_identity
    aks_kubelet_identity_roles_aks_acr
  ]
}

module aks_control_plane_identity 'aks-control-plane-identity/aks-control-plane-identity.bicep' = {
  name: 'aks-control-plane-identity'
  scope: resourceGroup('00000000-0000-0000-0000-000000000001', 'cluster-rg')
  params: {
    location: location
  }
}

module aks_kubelet_identity 'aks-kubelet-identity/aks-kubelet-identity.bicep' = {
  name: 'aks-kubelet-identity'
  scope: resourceGroup('00000000-0000-0000-0000-000000000001', 'cluster-rg')
  params: {
    location: location
  }
}

module aks_control_plane_identity_roles_aks_kubelet_identity 'aks-control-plane-identity-roles-aks-kubelet-identity/aks-control-plane-identity-roles-aks-kubelet-identity.bicep' = {
  name: 'aks-control-plane-identity-roles-aks-kubelet-identity'
  scope: resourceGroup('00000000-0000-0000-0000-000000000001', 'cluster-rg')
  params: {
    location: location
    aks_kubelet_identity_outputs_principalname: aks_kubelet_identity.outputs.principalName
    principalId: aks_control_plane_identity.outputs.principalId
  }
}

module aks_kubelet_identity_roles_aks_acr 'aks-kubelet-identity-roles-aks-acr/aks-kubelet-identity-roles-aks-acr.bicep' = {
  name: 'aks-kubelet-identity-roles-aks-acr'
  scope: rg
  params: {
    location: location
    aks_acr_outputs_name: aks_acr.outputs.name
    principalId: aks_kubelet_identity.outputs.principalId
  }
}