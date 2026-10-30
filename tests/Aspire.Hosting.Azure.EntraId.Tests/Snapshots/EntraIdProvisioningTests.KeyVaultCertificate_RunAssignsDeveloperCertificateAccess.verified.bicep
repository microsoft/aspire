@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param vault_outputs_name string

param principalType string

param principalId string

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: vault_outputs_name
}

resource vault_KeyVaultCertificateUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba'))
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'db79e9a7-68ee-4b58-9aeb-b90e7c24fcba')
    principalType: principalType
  }
  scope: vault
}