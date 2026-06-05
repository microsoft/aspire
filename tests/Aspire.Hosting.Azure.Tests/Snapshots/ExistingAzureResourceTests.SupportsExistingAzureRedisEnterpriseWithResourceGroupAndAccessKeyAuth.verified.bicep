@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param redis_kv_outputs_name string

resource redis 'Microsoft.Cache/redisEnterprise@2025-07-01' existing = {
  name: 'existingResourceName'
}

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: redis_kv_outputs_name
}

resource redis_default 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' = {
  name: 'default'
  properties: {
    port: 10000
  }
  parent: redis
}

resource connectionString 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'connectionstrings--redis'
  properties: {
    value: '${redis.properties.hostName}:10000,ssl=true,password=${redis_default.listKeys().primaryKey}'
  }
  parent: keyVault
}

resource primaryAccessKey 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  name: 'primaryaccesskey--redis'
  properties: {
    value: redis_default.listKeys().primaryKey
  }
  parent: keyVault
}

output name string = redis.name

output hostName string = redis.properties.hostName