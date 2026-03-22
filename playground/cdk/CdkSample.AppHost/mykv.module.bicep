@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

@secure()
param signaturesecret string

resource mykv 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: take('mykv-${uniqueString(resourceGroup().id)}', 24)
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
    'aspire-resource-name': 'mykv'
  }
}

resource mysecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'mysecret'
  properties: {
    value: signaturesecret
  }
  parent: mykv
}

output vaultUri string = mykv.properties.vaultUri

output name string = mykv.name

output id string = mykv.id