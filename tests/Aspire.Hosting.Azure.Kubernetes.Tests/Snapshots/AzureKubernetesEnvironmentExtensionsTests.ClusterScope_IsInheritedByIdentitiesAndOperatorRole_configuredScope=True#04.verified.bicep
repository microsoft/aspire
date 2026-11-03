@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource aks_control_plane_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('aks_control_plane_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = aks_control_plane_identity.id

output clientId string = aks_control_plane_identity.properties.clientId

output principalId string = aks_control_plane_identity.properties.principalId

output principalName string = aks_control_plane_identity.name

output name string = aks_control_plane_identity.name