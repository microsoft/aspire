@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param mykeyvault_outputs_name string

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
    accessKeysAuthentication: 'Enabled'
    port: 10000
  }
  parent: redis_cache
}

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: mykeyvault_outputs_name
}

resource connectionString 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'connectionstrings--redis-cache'
  properties: {
    value: '${redis_cache.properties.hostName}:10000,ssl=true,password=${redis_cache_default.listKeys().primaryKey}'
  }
  parent: keyVault
}

resource primaryAccessKey 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'primaryaccesskey--redis-cache'
  properties: {
    value: redis_cache_default.listKeys().primaryKey
  }
  parent: keyVault
}

output name string = redis_cache.name

output hostName string = redis_cache.properties.hostName