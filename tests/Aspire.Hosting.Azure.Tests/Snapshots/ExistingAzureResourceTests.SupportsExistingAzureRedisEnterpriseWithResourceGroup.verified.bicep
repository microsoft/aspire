@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

resource redis 'Microsoft.Cache/redisEnterprise@2025-07-01' existing = {
  name: existingResourceName
}

output connectionString string = '${redis.properties.hostName}:10000,ssl=true'

output name string = redis.name

output hostName string = redis.properties.hostName