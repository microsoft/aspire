@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

param administratorLogin string

@secure()
param administratorLoginPassword string

param postgressql_kv_outputs_name string

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

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: postgressql_kv_outputs_name
}

resource connectionString 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'connectionstrings--postgresSql'
  properties: {
    value: 'Host=${postgresSql.properties.fullyQualifiedDomainName};Username=${administratorLogin};Password=${administratorLoginPassword}'
  }
  parent: keyVault
}

output name string = postgresSql.name

output id string = postgresSql.id

output hostName string = postgresSql.properties.fullyQualifiedDomainName