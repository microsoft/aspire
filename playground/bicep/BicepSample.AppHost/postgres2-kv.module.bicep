@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource postgres2_kv 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: take('postgres2kv-${uniqueString(resourceGroup().id)}', 24)
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
    'aspire-resource-name': 'postgres2-kv'
  }
}

output vaultUri string = postgres2_kv.properties.vaultUri

output name string = postgres2_kv.name

output id string = postgres2_kv.id