@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' existing = {
  name: existingResourceName
}

resource sqlFirewallRule_AllowAllAzureIps 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  name: 'AllowAllAzureIps'
  properties: {
    endIpAddress: '0.0.0.0'
    startIpAddress: '0.0.0.0'
  }
  parent: sqlServer
}

output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName

output name string = sqlServer.name

output id string = sqlServer.id

output sqlServerAdminName string = sqlServer.properties.administrators.login