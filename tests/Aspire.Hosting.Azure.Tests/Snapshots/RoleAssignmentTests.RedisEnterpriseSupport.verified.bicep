@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param redis_outputs_name string

param principalId string

resource redis 'Microsoft.Cache/redisEnterprise@2025-07-01' existing = {
  name: redis_outputs_name
}

resource redis_default 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' existing = {
  name: 'default'
  parent: redis
}

resource redis_default_contributor 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-07-01' = {
  name: guid(redis_default.id, principalId, 'default')
  properties: {
    accessPolicyName: 'default'
    user: {
      objectId: principalId
    }
  }
  parent: redis_default
}