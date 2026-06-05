@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

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

output connectionString string = cosmos.properties.documentEndpoint

output name string = cosmos.name
