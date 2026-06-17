@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource kv3 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: take('kv3-${uniqueString(resourceGroup().id)}', 24)
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
    'aspire-resource-name': 'kv3'
  }
}

output vaultUri string = kv3.properties.vaultUri

output name string = kv3.name

output id string = kv3.id