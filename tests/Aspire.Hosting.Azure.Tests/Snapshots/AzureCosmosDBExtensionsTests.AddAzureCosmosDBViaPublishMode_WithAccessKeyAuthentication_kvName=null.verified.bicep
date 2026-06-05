@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param cosmos_kv_outputs_name string

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-08-15' = {
  name: take('cosmos-${uniqueString(resourceGroup().id)}', 44)
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
    'aspire-resource-name': 'cosmos'
  }
}

resource mydatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-08-15' = {
  name: 'mydatabase'
  location: location
  properties: {
    resource: {
      id: 'mydatabase'
    }
  }
  parent: cosmos
}

resource mycontainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-08-15' = {
  name: 'mycontainer'
  location: location
  properties: {
    resource: {
      id: 'mycontainer'
      partitionKey: {
        paths: [
          'mypartitionkeypath'
        ]
        kind: 'Hash'
      }
    }
  }
  parent: mydatabase
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

resource mydatabase_connectionString 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'connectionstrings--mydatabase'
  properties: {
    value: 'AccountEndpoint=${cosmos.properties.documentEndpoint};AccountKey=${cosmos.listKeys().primaryMasterKey};Database=mydatabase'
  }
  parent: keyVault
}

resource mycontainer_connectionString 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'connectionstrings--mycontainer'
  properties: {
    value: 'AccountEndpoint=${cosmos.properties.documentEndpoint};AccountKey=${cosmos.listKeys().primaryMasterKey};Database=mydatabase;Container=mycontainer'
  }
  parent: keyVault
}

output name string = cosmos.name