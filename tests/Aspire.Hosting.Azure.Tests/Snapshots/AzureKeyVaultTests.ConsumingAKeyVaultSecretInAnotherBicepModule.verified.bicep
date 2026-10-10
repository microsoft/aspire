@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param mykeyvault_outputs_name string

resource myKeyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: mykeyvault_outputs_name
}

resource myKeyVault_mySecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'mySecret'
  parent: myKeyVault
}

resource myKeyVault_mySecret2 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'mySecret2'
  parent: myKeyVault
}

output secretUri1 string = myKeyVault_mySecret.properties.secretUri

output secretUri2 string = myKeyVault_mySecret2.properties.secretUri