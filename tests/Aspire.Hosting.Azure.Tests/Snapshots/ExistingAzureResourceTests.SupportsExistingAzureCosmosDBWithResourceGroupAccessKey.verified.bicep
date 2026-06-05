@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

param cosmos_kv_outputs_name string

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-08-15' existing = {
  name: existingResourceName
}

resource mydb 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-08-15' = {
  name: 'mydb'
  location: location
  properties: {
    resource: {
      id: 'mydb'
    }
  }
  parent: cosmos
}

resource container 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-08-15' = {
  name: 'container'
  location: location
  properties: {
    resource: {
      id: 'container'
      partitionKey: {
        paths: [
          '/id'
        ]
        kind: 'Hash'
      }
    }
  }
  parent: mydb
}

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: cosmos_kv_outputs_name
}

resource connectionString 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'connectionstrings--cosmos'
  properties: {
    value: 'AccountEndpoint=${cosmos.properties.documentEndpoint};AccountKey=${cosmos.listKeys().primaryMasterKey}'
  }
  parent: keyVault
}

resource primaryAccessKey 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'primaryaccesskey--cosmos'
  properties: {
    value: '${cosmos.listKeys().primaryMasterKey}'
  }
  parent: keyVault
}

resource mydb_connectionString 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'connectionstrings--mydb'
  properties: {
    value: 'AccountEndpoint=${cosmos.properties.documentEndpoint};AccountKey=${cosmos.listKeys().primaryMasterKey};Database=mydb'
  }
  parent: keyVault
}

resource container_connectionString 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'connectionstrings--container'
  properties: {
    value: 'AccountEndpoint=${cosmos.properties.documentEndpoint};AccountKey=${cosmos.listKeys().primaryMasterKey};Database=mydb;Container=container'
  }
  parent: keyVault
}

output name string = cosmos.name