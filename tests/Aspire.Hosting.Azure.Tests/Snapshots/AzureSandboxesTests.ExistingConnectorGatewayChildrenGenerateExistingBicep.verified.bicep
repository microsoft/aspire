@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource gateway 'Microsoft.Web/connectorGateways@2026-05-01-preview' existing = {
  name: 'existing-gateway'
}

resource office365 'Microsoft.Web/connectorGateways/connections@2026-05-01-preview' existing = {
  name: 'existing-connection'
  parent: gateway
}

resource mcp 'Microsoft.Web/connectorGateways/mcpserverConfigs@2026-05-01-preview' existing = {
  name: 'existing-mcp'
  parent: gateway
}

output id string = gateway.id

output name string = gateway.name

output principalId string = gateway.identity.principalId

output tenantId string = gateway.identity.tenantId