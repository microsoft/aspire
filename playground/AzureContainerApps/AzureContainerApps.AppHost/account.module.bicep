@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param account_kv_outputs_name string

resource account 'Microsoft.DocumentDB/databaseAccounts@2024-08-15' = {
  name: take('account-${uniqueString(resourceGroup().id)}', 44)
  location: location
  properties: {
    locations: [
      {
        locationName: location
        failoverPriority: 0
      }
    ]
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    databaseAccountOfferType: 'Standard'
    disableLocalAuth: false
  }
  kind: 'GlobalDocumentDB'
  tags: {
    'aspire-resource-name': 'account'
  }
}

resource db 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-08-15' = {
  name: 'db'
  location: location
  properties: {
    resource: {
      id: 'db'
    }
  }
  parent: account
}

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: account_kv_outputs_name
}

resource connectionString 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'connectionstrings--account'
  properties: {
    value: 'AccountEndpoint=${account.properties.documentEndpoint};AccountKey=${account.listKeys().primaryMasterKey}'
  }
  parent: keyVault
}

resource primaryAccessKey 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'primaryaccesskey--account'
  properties: {
    value: '${account.listKeys().primaryMasterKey}'
  }
  parent: keyVault
}

resource db_connectionString 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'connectionstrings--db'
  properties: {
    value: 'AccountEndpoint=${account.properties.documentEndpoint};AccountKey=${account.listKeys().primaryMasterKey};Database=db'
  }
  parent: keyVault
}

output name string = account.name