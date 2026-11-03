targetScope = 'subscription'

param resourceGroupName string

param location string

param principalId string

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module production_control_plane_identity_roles_production_kubelet_identity 'production-control-plane-identity-roles-production-kubelet-identity/production-control-plane-identity-roles-production-kubelet-identity.bicep' = {
  name: 'production-control-plane-identity-roles-product-2bbbba63fb486680'
  scope: rg
  params: {
    location: location
  }
}