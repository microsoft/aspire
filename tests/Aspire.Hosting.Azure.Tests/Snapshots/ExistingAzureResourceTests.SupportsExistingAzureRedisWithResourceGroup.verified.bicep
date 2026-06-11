@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param existingResourceName string

resource redis 'Microsoft.Cache/redis@2024-11-01' existing = {
  name: existingResourceName
}

output connectionString string = '${redis.properties.hostName},ssl=true'

output name string = redis.name

output hostName string = redis.properties.hostName