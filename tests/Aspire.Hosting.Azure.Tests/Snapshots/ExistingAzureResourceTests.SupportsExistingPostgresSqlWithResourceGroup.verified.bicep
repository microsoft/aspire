@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

resource postgresSql 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' existing = {
  name: existingResourceName
}

resource postgreSqlFirewallRule_AllowAllAzureIps 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  name: 'AllowAllAzureIps'
  properties: {
    endIpAddress: '0.0.0.0'
    startIpAddress: '0.0.0.0'
  }
  parent: postgresSql
}

output connectionString string = 'Host=${postgresSql.properties.fullyQualifiedDomainName}'

output name string = postgresSql.name

output id string = postgresSql.id

output hostName string = postgresSql.properties.fullyQualifiedDomainName