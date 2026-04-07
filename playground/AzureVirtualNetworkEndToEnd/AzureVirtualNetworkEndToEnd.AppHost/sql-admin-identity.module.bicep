@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param sql_outputs_sqlserveradminname string

resource sql_admin_identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: sql_outputs_sqlserveradminname
}

output id string = sql_admin_identity.id

output clientId string = sql_admin_identity.properties.clientId

output principalId string = sql_admin_identity.properties.principalId

output principalName string = sql_admin_identity.name

output name string = sql_admin_identity.name