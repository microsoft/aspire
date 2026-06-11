@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param redis_cache_outputs_name string

param principalId string

resource redis_cache 'Microsoft.Cache/redisEnterprise@2025-07-01' existing = {
  name: redis_cache_outputs_name
}

resource redis_cache_default 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' existing = {
  name: 'default'
  parent: redis_cache
}

resource redis_cache_default_contributor 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-07-01' = {
  name: guid(redis_cache_default.id, principalId, 'default')
  properties: {
    accessPolicyName: 'default'
    user: {
      objectId: principalId
    }
  }
  parent: redis_cache_default
}