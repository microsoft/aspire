@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: existingResourceName
}

output vaultUri string = keyVault.properties.vaultUri

output name string = keyVault.name

output id string = keyVault.id