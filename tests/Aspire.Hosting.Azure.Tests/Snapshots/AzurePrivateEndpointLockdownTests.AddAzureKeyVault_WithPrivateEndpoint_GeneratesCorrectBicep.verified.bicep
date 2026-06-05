@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource keyvault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: take('keyvault-${uniqueString(resourceGroup().id)}', 24)
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    publicNetworkAccess: 'Disabled'
  }
  tags: {
    'aspire-resource-name': 'keyvault'
  }
}

output vaultUri string = keyvault.properties.vaultUri

output name string = keyvault.name

output id string = keyvault.id