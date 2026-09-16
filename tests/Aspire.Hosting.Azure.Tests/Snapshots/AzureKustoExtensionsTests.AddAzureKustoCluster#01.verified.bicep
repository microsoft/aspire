@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param kusto_outputs_name string

param principalId string

param principalType string

resource kusto 'Microsoft.Kusto/clusters@2025-02-14' existing = {
  name: kusto_outputs_name
}

resource testdb 'Microsoft.Kusto/clusters/databases@2025-02-14' existing = {
  name: 'testdb'
  parent: kusto
}

resource testdb_user 'Microsoft.Kusto/clusters/databases/principalAssignments@2025-02-14' = {
  name: guid(testdb.id, principalId, 'User')
  properties: {
    principalId: principalId
    role: 'User'
    principalType: (principalType == 'User') ? 'User' : (principalType == 'Group') ? 'Group' : 'App'
  }
  parent: testdb
}