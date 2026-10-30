@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param aks_kubelet_identity_outputs_principalname string

param principalId string

resource aks_kubelet_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: aks_kubelet_identity_outputs_principalname
}

resource aks_kubelet_identity_ManagedIdentityOperator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aks_kubelet_identity.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'f1a07417-d97a-45cb-824c-7a7467783830'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'f1a07417-d97a-45cb-824c-7a7467783830')
    principalType: 'ServicePrincipal'
  }
  scope: aks_kubelet_identity
}