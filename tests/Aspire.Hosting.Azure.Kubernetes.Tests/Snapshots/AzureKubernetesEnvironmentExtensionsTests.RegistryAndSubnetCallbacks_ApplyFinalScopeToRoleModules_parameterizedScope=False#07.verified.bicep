@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource aks_kubelet_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('aks_kubelet_identity-${uniqueString(resourceGroup().id)}', 128)
  location: location
}

output id string = aks_kubelet_identity.id

output clientId string = aks_kubelet_identity.properties.clientId

output principalId string = aks_kubelet_identity.properties.principalId

output principalName string = aks_kubelet_identity.name

output name string = aks_kubelet_identity.name