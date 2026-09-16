@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param kusto_outputs_name string

param principalId string

resource kusto 'Microsoft.Kusto/clusters@2025-02-14' existing = {
  name: kusto_outputs_name
}

resource db1 'Microsoft.Kusto/clusters/databases@2025-02-14' existing = {
  name: 'db1'
  parent: kusto
}

resource db1_user 'Microsoft.Kusto/clusters/databases/principalAssignments@2025-02-14' = {
  name: guid(db1.id, principalId, 'User')
  properties: {
    principalId: principalId
    role: 'User'
    principalType: 'App'
  }
  parent: db1
}

resource db2 'Microsoft.Kusto/clusters/databases@2025-02-14' existing = {
  name: 'db2'
  parent: kusto
}

resource db2_user 'Microsoft.Kusto/clusters/databases/principalAssignments@2025-02-14' = {
  name: guid(db2.id, principalId, 'User')
  properties: {
    principalId: principalId
    role: 'User'
    principalType: 'App'
  }
  parent: db2
}