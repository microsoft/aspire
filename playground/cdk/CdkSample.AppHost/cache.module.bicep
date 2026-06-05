@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource cache 'Microsoft.Cache/redisEnterprise@2025-07-01' = {
  name: take('cache-${uniqueString(resourceGroup().id)}', 60)
  location: location
  sku: {
    name: 'Balanced_B0'
  }
  properties: {
    minimumTlsVersion: '1.2'
  }
}

resource cache_default 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' = {
  name: 'default'
  properties: {
    accessKeysAuthentication: 'Disabled'
    port: 10000
  }
  parent: cache
}

output connectionString string = '${cache.properties.hostName}:10000,ssl=true'

output name string = cache.name

output hostName string = cache.properties.hostName