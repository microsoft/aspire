targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

param registry_group string

param registry_subscription string

param registry_name string

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
    aks_control_plane_identity_outputs_principalname: aks_control_plane_identity.outputs.principalName
    aks_kubelet_identity_outputs_principalname: aks_kubelet_identity.outputs.principalName
  }
  dependsOn: [
    aks_control_plane_identity_roles_aks_kubelet_identity
    aks_kubelet_identity_roles_registry
  ]
}

module aks_control_plane_identity 'aks-control-plane-identity/aks-control-plane-identity.bicep' = {
  name: 'aks-control-plane-identity'
  scope: rg
  params: {
    location: location
  }
}

module aks_kubelet_identity 'aks-kubelet-identity/aks-kubelet-identity.bicep' = {
  name: 'aks-kubelet-identity'
  scope: rg
  params: {
    location: location
  }
}

module aks_control_plane_identity_roles_aks_kubelet_identity 'aks-control-plane-identity-roles-aks-kubelet-identity/aks-control-plane-identity-roles-aks-kubelet-identity.bicep' = {
  name: 'aks-control-plane-identity-roles-aks-kubelet-identity'
  scope: rg
  params: {
    location: location
    aks_kubelet_identity_outputs_principalname: aks_kubelet_identity.outputs.principalName
    principalId: aks_control_plane_identity.outputs.principalId
  }
}

module aks_kubelet_identity_roles_registry 'aks-kubelet-identity-roles-registry/aks-kubelet-identity-roles-registry.bicep' = {
  name: 'aks-kubelet-identity-roles-registry'
  scope: resourceGroup(registry_subscription, registry_group)
  params: {
    location: location
    registry_name: registry_name
    principalId: aks_kubelet_identity.outputs.principalId
  }
}