@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param worker_identity_outputs_principalid string

resource gateway 'Microsoft.Web/connectorGateways@2026-05-01-preview' = {
  name: take('gateway${uniqueString(resourceGroup().id)}', 24)
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: { }
  tags: {
    'aspire-resource-name': 'gateway'
  }
}

resource connectorConnection_gateway_office365_ff65bf7e6a298940 'Microsoft.Web/connectorGateways/connections@2026-05-01-preview' = {
  name: 'office365-outlook'
  properties: {
    displayName: 'Office 365 Outlook'
    connectorName: 'office365'
  }
  parent: gateway
}

resource connectorAccessPolicy_gateway_office365_worker_access_4c96cd5e6ccf4c9e 'Microsoft.Web/connectorGateways/connections/accessPolicies@2026-05-01-preview' = {
  name: 'worker-acl'
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
  parent: connectorConnection_gateway_office365_ff65bf7e6a298940
}

resource connectorAccessPolicy_gateway_office365_worker_identity__e8ca2c1b62ef8e81 'Microsoft.Web/connectorGateways/connections/accessPolicies@2026-05-01-preview' = {
  name: 'worker-identity-acl'
  location: gateway.location
  properties: {
    principal: {
      type: 'ActiveDirectory'
      identity: {
        objectId: worker_identity_outputs_principalid
        tenantId: tenant().tenantId
      }
    }
  }
  parent: connectorConnection_gateway_office365_ff65bf7e6a298940
}

resource connectorMcpServer_gateway_outlook_mcp_7e6242840bbe075d 'Microsoft.Web/connectorGateways/mcpserverConfigs@2026-05-01-preview' = {
  name: 'outlook-tools'
  kind: 'ManagedMcpServer'
  properties: {
    description: 'Allow-listed Outlook tools.'
    state: 'Enabled'
    connectors: [
      {
        name: 'office365'
        connectionName: 'office365-outlook'
        displayName: 'office365'
        description: 'Read-only Outlook operations.'
        operations: [
          {
            name: 'GetEmailsV3'
            displayName: 'GetEmailsV3'
            description: 'Reads recent emails.'
          }
        ]
      }
    ]
  }
  parent: gateway
  dependsOn: [
    connectorConnection_gateway_office365_ff65bf7e6a298940
  ]
}

output id string = gateway.id

output name string = gateway.name

output principalId string = gateway.identity.principalId

output tenantId string = gateway.identity.tenantId