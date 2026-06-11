@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource pg_kv 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: take('pgkv-${uniqueString(resourceGroup().id)}', 24)
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
    'aspire-resource-name': 'pg-kv'
  }
}

output vaultUri string = pg_kv.properties.vaultUri

output name string = pg_kv.name

output id string = pg_kv.id