@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource pgsql2 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: take('pgsql2-${uniqueString(resourceGroup().id)}', 63)
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
    'aspire-resource-name': 'pgsql2'
  }
}

resource postgreSqlFirewallRule_AllowAllAzureIps 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  name: 'AllowAllAzureIps'
  properties: {
    endIpAddress: '0.0.0.0'
    startIpAddress: '0.0.0.0'
  }
  parent: pgsql2
}

resource pgsql2db 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  name: 'pgsql2db'
  parent: pgsql2
}

output connectionString string = 'Host=${pgsql2.properties.fullyQualifiedDomainName}'

output name string = pgsql2.name

output id string = pgsql2.id

output hostName string = pgsql2.properties.fullyQualifiedDomainName