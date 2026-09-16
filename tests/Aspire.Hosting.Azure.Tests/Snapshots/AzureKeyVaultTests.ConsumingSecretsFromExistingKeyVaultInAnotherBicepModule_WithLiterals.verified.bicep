@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource kv 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: 'literalKvName'
  scope: resourceGroup('literalRgName')
}

resource kv_mySecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'mySecret'
  parent: kv
}

resource kv_mySecret2 'Microsoft.KeyVault/vaults/secrets@2024-11-01' existing = {
  name: 'mySecret2'
  parent: kv
}

output secretUri1 string = kv_mySecret.properties.secretUri

output secretUri2 string = kv_mySecret2.properties.secretUri