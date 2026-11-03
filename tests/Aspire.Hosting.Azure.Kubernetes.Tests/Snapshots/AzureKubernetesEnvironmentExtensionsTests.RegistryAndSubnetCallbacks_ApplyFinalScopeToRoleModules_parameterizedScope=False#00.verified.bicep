targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module registry 'registry/registry.bicep' = {
  name: 'registry'
  scope: resourceGroup('00000000-0000-0000-0000-000000000001', 'registry-rg')
  params: {
    location: location
  }
}

module network 'network/network.bicep' = {
  name: 'network'
  scope: resourceGroup('00000000-0000-0000-0000-000000000002', 'network-rg')
  params: {
    location: location
  }
}

module aks 'aks/aks.bicep' = {
  name: 'aks'
  scope: rg
  params: {
    location: location
    aks_control_plane_identity_outputs_principalname: aks_control_plane_identity.outputs.principalName
    aks_kubelet_identity_outputs_principalname: aks_kubelet_identity.outputs.principalName
    subnetId: network.outputs.subnet_Id
  }
  dependsOn: [
    aks_control_plane_identity_roles_aks_kubelet_identity
    aks_control_plane_identity_roles_network
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

module aks_control_plane_identity_roles_network 'aks-control-plane-identity-roles-network/aks-control-plane-identity-roles-network.bicep' = {
  name: 'aks-control-plane-identity-roles-network'
  scope: resourceGroup('00000000-0000-0000-0000-000000000002', 'network-rg')
  params: {
    location: location
    principalId: aks_control_plane_identity.outputs.principalId
  }
}

module aks_kubelet_identity_roles_registry 'aks-kubelet-identity-roles-registry/aks-kubelet-identity-roles-registry.bicep' = {
  name: 'aks-kubelet-identity-roles-registry'
  scope: resourceGroup('00000000-0000-0000-0000-000000000001', 'registry-rg')
  params: {
    location: location
    principalId: aks_kubelet_identity.outputs.principalId
  }
}