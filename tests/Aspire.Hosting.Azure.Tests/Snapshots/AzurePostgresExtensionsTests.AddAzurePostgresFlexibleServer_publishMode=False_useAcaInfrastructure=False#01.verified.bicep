@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param postgres_data_outputs_name string

param principalType string

param principalId string

param principalName string

resource postgres_data 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' existing = {
  name: postgres_data_outputs_name
}

resource postgres_data_admin 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2024-08-01' = {
  name: principalId
  properties: {
    principalName: principalName
    principalType: principalType
  }
  parent: postgres_data
}