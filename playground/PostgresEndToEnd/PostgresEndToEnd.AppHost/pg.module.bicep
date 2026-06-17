@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource pg 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: take('pg-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Disabled'
    }
    availabilityZone: '1'
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    storage: {
      storageSizeGB: 32
    }
    version: '16'
  }
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  tags: {
    'aspire-resource-name': 'pg'
  }
}

resource postgreSqlFirewallRule_AllowAllAzureIps 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  name: 'AllowAllAzureIps'
  properties: {
    endIpAddress: '0.0.0.0'
    startIpAddress: '0.0.0.0'
  }
  parent: pg
}

resource db1 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  name: 'db1'
  parent: pg
}

output connectionString string = 'Host=${pg.properties.fullyQualifiedDomainName}'

output name string = pg.name

output id string = pg.id

output hostName string = pg.properties.fullyQualifiedDomainName