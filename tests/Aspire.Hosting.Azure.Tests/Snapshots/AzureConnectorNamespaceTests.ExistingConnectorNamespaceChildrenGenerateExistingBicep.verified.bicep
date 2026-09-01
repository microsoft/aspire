@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

resource gateway 'Microsoft.Web/connectorGateways@2026-05-01-preview' existing = {
  name: 'existing-gateway'
}

resource connectorConnection_gateway_office365_ff65bf7e6a298940 'Microsoft.Web/connectorGateways/connections@2026-05-01-preview' existing = {
  name: 'existing-connection'
  parent: gateway
}

resource connectorConnection_gateway_sharepoint_e2b458120659a04f 'Microsoft.Web/connectorGateways/connections@2026-05-01-preview' = {
  name: 'sharepoint'
  properties: {
    displayName: 'sharepoint'
    connectorName: 'sharepointonline'
  }
  parent: gateway
}

resource connectorAccessPolicy_gateway_sharepoint_reader_1c0b87931d6065d9 'Microsoft.Web/connectorGateways/connections/accessPolicies@2026-05-01-preview' = {
  name: 'reader'
  location: location
  properties: {
    principal: {
      type: 'ActiveDirectory'
      identity: {
        objectId: '11111111-1111-1111-1111-111111111111'
        tenantId: '22222222-2222-2222-2222-222222222222'
      }
    }
  }
  parent: connectorConnection_gateway_sharepoint_e2b458120659a04f
}

resource connectorMcpServer_gateway_mcp_7fe2af054c40f74d 'Microsoft.Web/connectorGateways/mcpserverConfigs@2026-05-01-preview' existing = {
  name: 'existing-mcp'
  parent: gateway
}

output id string = gateway.id

output name string = gateway.name

output principalId string = ''

output tenantId string = ''