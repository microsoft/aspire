@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource account_kv 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: take('accountkv-${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
  }
  tags: {
    'aspire-resource-name': 'account-kv'
  }
}

output vaultUri string = account_kv.properties.vaultUri

output name string = account_kv.name

output id string = account_kv.id