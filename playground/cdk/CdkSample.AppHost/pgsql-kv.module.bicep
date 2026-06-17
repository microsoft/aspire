@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource pgsql_kv 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: take('pgsqlkv-${uniqueString(resourceGroup().id)}', 24)
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
    'aspire-resource-name': 'pgsql-kv'
  }
}

output vaultUri string = pgsql_kv.properties.vaultUri

output name string = pgsql_kv.name

output id string = pgsql_kv.id