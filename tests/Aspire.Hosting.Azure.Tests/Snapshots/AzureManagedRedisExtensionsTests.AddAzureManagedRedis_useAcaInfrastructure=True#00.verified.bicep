@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource redis_cache 'Microsoft.Cache/redisEnterprise@2025-07-01' = {
  name: take('rediscache-${uniqueString(resourceGroup().id)}', 60)
  location: location
  sku: {
    name: 'Balanced_B0'
  }
  properties: {
    minimumTlsVersion: '1.2'
  }
}

resource redis_cache_default 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' = {
  name: 'default'
  properties: {
    accessKeysAuthentication: 'Disabled'
    port: 10000
  }
  parent: redis_cache
}

output connectionString string = '${redis_cache.properties.hostName}:10000,ssl=true'

output name string = redis_cache.name

output hostName string = redis_cache.properties.hostName