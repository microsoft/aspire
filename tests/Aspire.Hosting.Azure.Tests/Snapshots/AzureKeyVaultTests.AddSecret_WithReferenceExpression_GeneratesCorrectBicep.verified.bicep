@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

@secure()
param server_localhost_database_mydb_pwd_password_value string

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

resource secret_connection_string 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'connection-string'
  properties: {
    value: server_localhost_database_mydb_pwd_password_value
  }
  parent: mykv
}

output vaultUri string = mykv.properties.vaultUri

output name string = mykv.name

output id string = mykv.id