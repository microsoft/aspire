@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param cosmosdb_outputs_name string

param principalId string

resource cosmosdb 'Microsoft.DocumentDB/databaseAccounts@2024-08-15' existing = {
  name: cosmosdb_outputs_name
}

resource cosmosdb_roleDefinition 'Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions@2024-08-15' existing = {
  name: '00000000-0000-0000-0000-000000000002'
  parent: cosmosdb
}

resource cosmosdb_roleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-08-15' = {
  name: guid(principalId, cosmosdb_roleDefinition.id, cosmosdb.id)
  properties: {
    principalId: principalId
    roleDefinitionId: cosmosdb_roleDefinition.id
    scope: cosmosdb.id
  }
  parent: cosmosdb
}