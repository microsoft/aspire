@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource gateway 'Microsoft.Web/connectorGateways@2026-05-01-preview' existing = {
  name: 'existing-gateway'
}

resource office365 'Microsoft.Web/connectorGateways/connections@2026-05-01-preview' existing = {
  name: 'existing-connection'
  parent: gateway
}

resource sharepoint 'Microsoft.Web/connectorGateways/connections@2026-05-01-preview' = {
  name: 'sharepoint'
  properties: {
    displayName: 'sharepoint'
    connectorName: 'sharepointonline'
  }
  parent: gateway
}

resource sharepoint_policy_reader_1cd0856cb9868404 'Microsoft.Web/connectorGateways/connections/accessPolicies@2026-05-01-preview' = {
  name: 'reader'
  location: gateway.location
  properties: {
    principal: {
      type: 'ActiveDirectory'
      identity: {
        objectId: '11111111-1111-1111-1111-111111111111'
        tenantId: '22222222-2222-2222-2222-222222222222'
      }
    }
  }
  parent: sharepoint
}

resource mcp 'Microsoft.Web/connectorGateways/mcpserverConfigs@2026-05-01-preview' existing = {
  name: 'existing-mcp'
  parent: gateway
}

output id string = gateway.id

output name string = gateway.name

output principalId string = ''

output tenantId string = ''