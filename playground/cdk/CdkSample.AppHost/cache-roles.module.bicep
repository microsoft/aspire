@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param cache_outputs_name string

param principalId string

resource cache 'Microsoft.Cache/redisEnterprise@2025-07-01' existing = {
  name: cache_outputs_name
}

resource cache_default 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' existing = {
  name: 'default'
  parent: cache
}

resource cache_default_contributor 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-07-01' = {
  name: guid(cache_default.id, principalId, 'default')
  properties: {
    accessPolicyName: 'default'
    user: {
      objectId: principalId
    }
  }
  parent: cache_default
}