@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource gateway 'Microsoft.Web/connectorGateways@2026-05-01-preview' = {
  name: take('gateway${uniqueString(resourceGroup().id)}', 24)
  location: resourceGroup().location
  identity: {
    type: 'SystemAssigned'
  }
  tags: {
    'aspire-resource-name': 'gateway'
  }
}

resource office365 'Microsoft.Web/connectorGateways/connections@2026-05-01-preview' = {
  name: 'office365'
  properties: {
    displayName: 'Office 365 (Outlook)'
    connectorName: 'Office365'
  }
  parent: gateway
}

resource teams 'Microsoft.Web/connectorGateways/connections@2026-05-01-preview' = {
  name: 'teams'
  properties: {
    displayName: 'Microsoft Teams (Work IQ MCP)'
    connectorName: 'a365teamsmcp'
  }
  parent: gateway
}

resource teamsmcp 'Microsoft.Web/connectorGateways/mcpserverConfigs@2026-05-01-preview' = {
  name: 'teamsmcp'
  kind: 'ManagedMcpServer'
  properties: {
    description: 'Microsoft Teams MCP server'
    connectors: [
      {
        name: 'a365teamsmcp'
        connectionName: 'teams'
        operations: [
          {
            name: 'mcp_TeamsServer'
            displayName: 'Microsoft Teams MCP Server'
            description: 'Upstream MCP endpoint that proxies JSON-RPC traffic to the Work IQ Teams MCP server.'
          }
        ]
      }
    ]
  }
  parent: gateway
  dependsOn: [
    teams
  ]
}

output id string = gateway.id

output name string = gateway.name

output principalId string = gateway.identity.principalId

output tenantId string = gateway.identity.tenantId